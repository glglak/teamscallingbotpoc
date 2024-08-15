// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using CallingBotSample.Authentication;
using CallingBotSample.Cache;
using CallingBotSample.Models;
using CallingBotSample.Options;
using CallingBotSample.Services.BotFramework;
using CallingBotSample.Services.CognitiveServices;
using CallingBotSample.Services.MicrosoftGraph;
using CallingBotSample.Services.TeamsRecordingService;
using CallingBotSample.Utility;
using CallingMeetingBot.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Core.Notifications;
using Microsoft.Graph.Communications.Core.Serialization;

namespace CallingBotSample.Bots
{
    public class CallingBot : ActivityHandler
    {
        private readonly IGraphLogger graphLogger;
        private readonly IRequestAuthenticationProvider authenticationProvider;
        private readonly INotificationProcessor notificationProcessor;
        private readonly CommsSerializer serializer;
        private readonly BotOptions botOptions;
        private readonly ICallService callService;
        private readonly AudioRecordingConstants audioRecordingConstants;
        private readonly ITeamsRecordingService teamsRecordingService;
        private readonly ICallCache callCache;
        private readonly IIncidentCache incidentCache;
        private readonly ISpeechService speechService;
        private readonly IBotService botService;
        private readonly ILogger<CallingBot> logger;
        private readonly GraphServiceClient graphServiceClient;

        public CallingBot(
            ICallService callService,
            AudioRecordingConstants audioRecordingConstants,
            ITeamsRecordingService teamsRecordingService,
            IGraphLogger graphLogger,
            ICallCache callCache,
            IIncidentCache incidentCache,
            ISpeechService speechService,
            IBotService botService,
            IOptions<BotOptions> botOptions,
            ILogger<CallingBot> logger,
            GraphServiceClient graphServiceClient)
        {
            this.botOptions = botOptions.Value;
            this.callService = callService;
            this.audioRecordingConstants = audioRecordingConstants;
            this.teamsRecordingService = teamsRecordingService;
            this.graphLogger = graphLogger;
            this.callCache = callCache;
            this.incidentCache = incidentCache;
            this.speechService = speechService;
            this.botService = botService;
            this.logger = logger;
            this.graphServiceClient = graphServiceClient;

            var name = this.GetType().Assembly.GetName().Name;
            authenticationProvider = new AuthenticationProvider(name, this.botOptions.AppId, this.botOptions.AppSecret, graphLogger);

            serializer = new CommsSerializer();
            notificationProcessor = new NotificationProcessor(serializer);
            notificationProcessor.OnNotificationReceived += this.NotificationProcessor_OnNotificationReceived;
        }

        public async Task ProcessNotificationAsync(HttpRequest request, HttpResponse response)
        {
            try
            {
                var httpRequest = request.CreateRequestMessage();
                var results = await authenticationProvider.ValidateInboundRequestAsync(httpRequest).ConfigureAwait(false);
                if (results.IsValid)
                {
                    var httpResponse = await notificationProcessor.ProcessNotificationAsync(httpRequest).ConfigureAwait(false);
                    await httpResponse.CreateHttpResponseAsync(response).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = StatusCodes.Status403Forbidden;
                }
            }
            catch (Exception e)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await response.WriteAsync(e.ToString()).ConfigureAwait(false);
            }
        }
         
        private void NotificationProcessor_OnNotificationReceived(NotificationEventArgs args)
        {
            _ = NotificationProcessor_OnNotificationReceivedAsync(args).ForgetAndLogExceptionAsync(
              graphLogger,
              $"Error processing notification {args.Notification.ResourceUrl} with scenario {args.ScenarioId}");
        }

        private async Task NotificationProcessor_OnNotificationReceivedAsync(NotificationEventArgs args)
        {
            graphLogger.CorrelationId = args.ScenarioId;
            var callId = GetCallIdFromNotification(args);

            if (args.ResourceData is Call call)
            {
                if (args.ChangeType == ChangeType.Created && call.State == CallState.Incoming)
                {
                    await callService.Answer(callId, new List<MediaInfo>());
                }
                else if (args.ChangeType == ChangeType.Updated && call.State == CallState.Established)
                {
                    if (!callCache.GetIsEstablished(callId))
                    {
                        callCache.SetIsEstablished(callId);
                        await StartContinuousRecording(callId);  // Start recording immediately
                    }
                }
            }
            else if (args.ResourceData is RecordOperation recording)
            {
                if (recording.ResultInfo.Code >= 400)
                {
                    return;
                }

                await ProcessRecordingOperation(callId, recording);

                // Start the next chunk recording
                await StartRecordingInChunks(callId);
            }
        }

        private async Task StartRecordingInChunks(string callId)
        {
            bool isRecording = true;

            while (isRecording)
            {
                try
                {
                    await LogToBlobAsync($"Starting 10-second recording chunk for call: {callId}", "start_recording");

                    var recordingOperation = await callService.Record(callId, null, 10);

                    if (recordingOperation == null || string.IsNullOrEmpty(recordingOperation.Id))
                    {
                        await LogToBlobAsync($"Failed to start recording for call: {callId}", "recording_error");
                        isRecording = false;
                        continue;
                    }

                    await LogToBlobAsync($"Recording started successfully for call: {callId}. Operation ID: {recordingOperation.Id}", "recording_success");

                    await Task.Delay(10000); // 10 seconds

                    if (await IsRecordingComplete(recordingOperation))
                    {
                        await ProcessRecordingOperation(callId, recordingOperation);
                    }
                    else
                    {
                        await LogToBlobAsync($"Recording did not complete successfully for call: {callId}", "recording_incomplete");
                    }
                }
                catch (Exception ex)
                {
                    await LogToBlobAsync($"Error during recording in chunks: {ex.Message}", "recording_exception");
                    isRecording = false;
                }
            }
        }
        private async Task LogToBlobAsync(string message, string logFileName)
        {
            string storageConnectionString = "BlobEndpoint=https://callingbotrecordings.blob.core.windows.net/;QueueEndpoint=https://callingbotrecordings.queue.core.windows.net/;FileEndpoint=https://callingbotrecordings.file.core.windows.net/;TableEndpoint=https://callingbotrecordings.table.core.windows.net/;SharedAccessSignature=sv=2022-11-02&ss=bfqt&srt=co&sp=rwdlacupiytfx&se=2024-12-05T18:13:24Z&st=2024-08-14T09:13:24Z&spr=https&sig=2uctt8fL%2BvxbKUwNq9WO7W4HWdjprBEjA2FvMy2%2FAB4%3D";
            string containerName = "botlogs";

            BlobServiceClient blobServiceClient = new BlobServiceClient(storageConnectionString);
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            await containerClient.CreateIfNotExistsAsync();

            string blobName = $"{logFileName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log";

            BlobClient blobClient = containerClient.GetBlobClient(blobName);

            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(message)))
            {
                await blobClient.UploadAsync(stream);
            }
        }


        private async Task StartContinuousRecording(string callId)
        {
            bool isRecording = true;

            while (isRecording)
            {
                try
                {
                    // Start a 10-second recording
                    var recordingOperation = await callService.Record(callId, null, 10);

                    // Wait for the recording to complete
                    await Task.Delay(10000); // 10 seconds

                    // Process the recording once it's completed
                    await ProcessRecordingOperation(callId, recordingOperation);

                    // Optionally, add any logic here to determine if recording should stop
                    // isRecording = ShouldStopRecording();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during continuous recording.");
                    isRecording = false;
                }
            }
        }


        private async Task<bool> IsRecordingComplete(RecordOperation recordingOperation)
        {
            try
            {
                // Poll for operation status
                var operation = await graphServiceClient.Communications.Calls[recordingOperation.Id]
                    .Request()
                    .GetAsync();

                // Check if the operation has completed
                await LogToBlobAsync($"Failed to start operation for call: {operation.ResultInfo.Code}", "recording_error");
                return operation.ResultInfo.Code == (int)OperationStatus.Completed;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking recording operation status.");
                return false;
            }
        }

        private async Task ProcessRecordingOperation(string callId, RecordOperation recording)
        {
            var recordingLocation = await teamsRecordingService.DownloadRecording(recording.RecordingLocation, recording.RecordingAccessToken);
            var result = await speechService.ConvertWavToText(recordingLocation);

            // Upload transcription to Blob Storage
            string blobUri = await UploadToBlobAsync("transcription", System.Text.Encoding.UTF8.GetBytes(result));

            var callDetails = await callService.Get(callId);
            var threadId = callDetails?.ChatInfo?.ThreadId;

            if (!string.IsNullOrEmpty(threadId))
            {
                await botService.SendToConversation($"Audio saved: {blobUri}", threadId);
            }

            // Optional: Play the saved recording back in the call
            await callService.PlayPrompt(
                callId,
                new List<MediaInfo>
                {
            new MediaInfo
            {
                Uri = new Uri(botOptions.BotBaseUrl, recordingLocation).ToString(),
                ResourceId = Guid.NewGuid().ToString(),
            }
                });
        }


        private async Task<string> UploadToBlobAsync(string fileName, byte[] fileBytes)
        {
            string storageConnectionString = "BlobEndpoint=https://callingbotrecordings.blob.core.windows.net/;QueueEndpoint=https://callingbotrecordings.queue.core.windows.net/;FileEndpoint=https://callingbotrecordings.file.core.windows.net/;TableEndpoint=https://callingbotrecordings.table.core.windows.net/;SharedAccessSignature=sv=2022-11-02&ss=bfqt&srt=co&sp=rwdlacupiytfx&se=2024-12-05T18:13:24Z&st=2024-08-14T09:13:24Z&spr=https&sig=2uctt8fL%2BvxbKUwNq9WO7W4HWdjprBEjA2FvMy2%2FAB4%3D";
            string containerName = "transcriptions";

            BlobServiceClient blobServiceClient = new BlobServiceClient(storageConnectionString);
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            await containerClient.CreateIfNotExistsAsync();

            string blobName = $"{fileName}_{Guid.NewGuid()}";

            BlobClient blobClient = containerClient.GetBlobClient(blobName);

            using (var stream = new MemoryStream(fileBytes))
            {
                await blobClient.UploadAsync(stream);
            }

            return blobClient.Uri.ToString();
        }

        private string GetCallIdFromNotification(NotificationEventArgs notificationArgs)
        {
            if (notificationArgs.ResourceData is CommsOperation operation && !string.IsNullOrEmpty(operation.ClientContext))
            {
                return operation.ClientContext;
            }

            return notificationArgs.Notification.ResourceUrl.Split('/')[3];
        }
    }
}
