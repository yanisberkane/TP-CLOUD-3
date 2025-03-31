using Azure.Core;
using Microsoft.Extensions.Options;
using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.AI.ContentSafety;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using MVC.Models;
using Azure;
using Worker_Content.Services;
using SharedEvents.Events;

namespace Worker_Content
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly EventHubService _eventHub;
        private ServiceBusClient _serviceBusClient;
        private ServiceBusProcessor _processor;
        private ConcurrentQueue<ServiceBusReceivedMessage> _messageQueue;
        private WorkerOptions _options;
        private BlobServiceClient _blobServiceClient;
        private ContentSafetyClient _contentSafetyClient;
        private SemaphoreSlim _semaphore;

        private const int ConcurentJobLimit = 1;

        public Worker(ILogger<Worker> logger, IOptions<WorkerOptions> options, EventHubService eventHub)
        {
            _logger = logger;
            _eventHub = eventHub;
            _options = options.Value;

            _contentSafetyClient = new ContentSafetyClient(new Uri(_options.ContentSafetyEndpoint), new AzureKeyCredential(_options.ContentSafetyKey));

            BlobClientOptions blobClientOptions = new BlobClientOptions
            {
                Retry = {
                    Delay = TimeSpan.FromSeconds(2),
                    MaxRetries = 5,
                    Mode = RetryMode.Exponential,
                    MaxDelay = TimeSpan.FromSeconds(10)
                },
            };

            _blobServiceClient = new BlobServiceClient(_options.BlobStorageKey, blobClientOptions);

            _messageQueue = new ConcurrentQueue<ServiceBusReceivedMessage>();

            string queueName = "contentsafetymessage";

            ServiceBusClientOptions clientOptions = new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    Delay = TimeSpan.FromSeconds(10),
                    MaxDelay = TimeSpan.FromSeconds(60),
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 6,
                },
                TransportType = ServiceBusTransportType.AmqpWebSockets,
                ConnectionIdleTimeout = TimeSpan.FromMinutes(10)
            };

            _serviceBusClient = new ServiceBusClient(_options.ServiceBusKey, clientOptions);
            _processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 5,
                AutoCompleteMessages = false
            });

            _processor.ProcessMessageAsync += MessageHandler;
            _processor.ProcessErrorAsync += ErrorHandler;

            _semaphore = new SemaphoreSlim(ConcurentJobLimit);
        }

        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            await _semaphore.WaitAsync();
            _messageQueue.Enqueue(args.Message);

            _ = ProcessMessagesAsync(args);
        }

        private async Task ProcessMessagesAsync(ProcessMessageEventArgs args)
        {
            try
            {
                var message = JsonSerializer.Deserialize<ContentTypeValidation>(args.Message.Body.ToString())!;
                if (message == null) throw new InvalidOperationException("Message deserialization failed.");

                _logger.LogInformation($"Processing message: {args.Message.MessageId}, PostId: {message.PostId}, ContentType: {message.ContentType}");

                bool Approved;
                Uri? uri = null;

                switch (message.ContentType)
                {
                    case MVC.Models.ContentType.Text:
                        Approved = await ProcessTextValidationAsync(message.Content);
                        break;

                    case MVC.Models.ContentType.Image:
                        using (MemoryStream ms = new MemoryStream())
                        {
                            Approved = await ProcessImageValidationAsync(message.Content, ms);
                            uri = await UploadImageAsync(message.Content, ms);
                        }
                        await DeleteImageAsync(message.Content);
                        break;

                    default:
                        throw new InvalidOperationException("Unsupported ContentType.");
                }

                // Send event to Event Hub
                var evt = new ContentValidatedEvent
                {
                    PostId = message.PostId,
                    BlobImage = message.PostId,
                    IsValid = Approved,
                    Reason = Approved ? null : "Content validation failed."
                };

                await _eventHub.SendEventAsync(evt);

                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing message: {args.Message.MessageId}");
                await HandleMessageErrorAsync(args);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task DeleteImageAsync(string imageId)
        {
            var blob = _blobServiceClient.GetBlobContainerClient(_options.BlobContainer1).GetBlockBlobClient(imageId);
            await blob.DeleteAsync();
        }

        private async Task<Uri> UploadImageAsync(string imageId, MemoryStream ms)
        {
            var blob = _blobServiceClient.GetBlobContainerClient(_options.BlobContainer2).GetBlockBlobClient(imageId);
            ms.Position = 0;
            await blob.UploadAsync(ms);
            return blob.Uri;
        }

        private async Task<bool> ProcessTextValidationAsync(string Text)
        {
            try
            {
                var request = new AnalyzeTextOptions(Text);
                var response = await _contentSafetyClient.AnalyzeTextAsync(request);
                return CheckTextSeverity(response.Value.CategoriesAnalysis, "Hate", "SelfHarm", "Sexual", "Violence");
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError("Analyze text failed. Status: {0}, Code: {1}, Message: {2}", ex.Status, ex.ErrorCode, ex.Message);
                throw;
            }
        }

        private bool CheckTextSeverity(IReadOnlyList<TextCategoriesAnalysis> analysis, params string[] categories)
        {
            foreach (var category in categories)
            {
                int severity = analysis.FirstOrDefault(a => a.Category == category)?.Severity ?? 0;
                if (severity > 0) return true;
            }
            return false;
        }

        private async Task<bool> ProcessImageValidationAsync(string imageId, MemoryStream ms)
        {
            var blob = _blobServiceClient.GetBlobContainerClient(_options.BlobContainer1).GetBlockBlobClient(imageId);
            await blob.DownloadToAsync(ms);
            ms.Position = 0;

            var image = new ContentSafetyImageData(BinaryData.FromStream(ms));
            var request = new AnalyzeImageOptions(image);

            try
            {
                var response = await _contentSafetyClient.AnalyzeImageAsync(request);
                return CheckImageSeverity(response.Value.CategoriesAnalysis, "Hate", "SelfHarm", "Sexual", "Violence");
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError("Analyze image failed. Status: {0}, Code: {1}, Message: {2}", ex.Status, ex.ErrorCode, ex.Message);
                throw;
            }
        }

        private bool CheckImageSeverity(IReadOnlyList<ImageCategoriesAnalysis> analysis, params string[] categories)
        {
            foreach (var category in categories)
            {
                int severity = analysis.FirstOrDefault(a => a.Category == category)?.Severity ?? 0;
                if (severity > 0) return true;
            }
            return false;
        }

        private async Task HandleMessageErrorAsync(ProcessMessageEventArgs args)
        {
            if (args.Message.DeliveryCount > 5)
            {
                await args.DeadLetterMessageAsync(args.Message, "Processing Error", "Exceed Maximum retries");
            }
            else
            {
                await args.AbandonMessageAsync(args.Message);
            }
        }

        private Task ErrorHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Error processing messages.");
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _processor.StartProcessingAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(5000, stoppingToken);
            }
            await _processor.StopProcessingAsync(stoppingToken);
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            await _processor.CloseAsync();
            await base.StopAsync(stoppingToken);
        }
    }
}
