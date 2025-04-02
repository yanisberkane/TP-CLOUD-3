using Azure.Core;

//DI
using Microsoft.Extensions.Options;

//Service Bus
using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;

//Blob
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

//Images
using Azure.AI.ContentSafety;

//Serialization
using System.Text.Json;
using Microsoft.Azure.Cosmos;

//ContentTypeValidation model
using MVC.Models;
using Azure;

namespace Worker_Content
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private ServiceBusClient _serviceBusClient;
        private ServiceBusProcessor _processor;
        private ConcurrentQueue<ServiceBusReceivedMessage> _messageQueue;
        private WorkerOptions _options;
        private BlobServiceClient _blobServiceClient;
        private CosmosClient _cosmosClient;
        private Container _containerPosts;
        private Container _containerComments;
        private ContentSafetyClient _contentSafetyClient;
        private SemaphoreSlim _semaphore;

        private const int ConcurentJobLimit = 1;

        public Worker(ILogger<Worker> logger, IOptions<WorkerOptions> options)
        {
            _logger = logger;
            _options = options.Value;

            // Content Safety
            _contentSafetyClient = new ContentSafetyClient(new Uri(_options.ContentSafetyEndpoint), new AzureKeyCredential(_options.ContentSafetyKey));

            // CosmosDb ...
            CosmosClientOptions cosmosClientOptions = new CosmosClientOptions
            {
                MaxRetryAttemptsOnRateLimitedRequests = 9,      // MaxRetryAttemptsOnThrottledRequests: Maximum number of retry attempts on throttled requests
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),   // MaxRetryWaitTimeOnThrottledRequests: Maximum wait time for the retry attempts
                RequestTimeout = TimeSpan.FromSeconds(60),      // RequestTimeout: Sets the request timeout for network operations
                ConnectionMode = ConnectionMode.Direct,         // ConnectionMode: Use Direct mode for better performance and Gateway mode for improved resilience
                EnableTcpConnectionEndpointRediscovery = true   // EnableTcpConnectionEndpointRediscovery: Enable endpoint rediscovery in the case of connection failures
            };

            // Pour créer la connection au CosmosDB nous devons avoir le DatabaseId et le ContainerID
            // DatabaseId = ApplicationDb
            // ContainerId = Posts et Comments
            _cosmosClient = new CosmosClient(_options.CosmosDbKey, cosmosClientOptions);
            _containerPosts = _cosmosClient.GetContainer("ApplicationDB", "Posts");
            _containerComments = _cosmosClient.GetContainer("ApplicationDB", "Comments");

            // Blob ...
            BlobClientOptions blobClientOptions = new BlobClientOptions
            {
                Retry = {
                        Delay = TimeSpan.FromSeconds(2),     //The delay between retry attempts for a fixed approach or the delay on which to base
                                                             //calculations for a backoff-based approach
                        MaxRetries = 5,                      //The maximum number of retry attempts before giving up
                        Mode = RetryMode.Exponential,        //The approach to use for calculating retry delays
                        MaxDelay = TimeSpan.FromSeconds(10)  //The maximum permissible delay between retry attempts
                        },
            };

            _blobServiceClient = new BlobServiceClient(_options.BlobStorageKey, blobClientOptions);

            // Service Bus ...
            _messageQueue = new ConcurrentQueue<ServiceBusReceivedMessage>();

            // Hardcoded
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
                ConnectionIdleTimeout = TimeSpan.FromMinutes(10)   //Défault = 1 minutes
            };

            _serviceBusClient = new ServiceBusClient(_options.ServiceBusKey, clientOptions);
            _processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 5,
                AutoCompleteMessages = false
            });

            _processor.ProcessMessageAsync += MessageHandler;
            _processor.ProcessErrorAsync += ErrorHandler;

            _semaphore = new SemaphoreSlim(ConcurentJobLimit); // Limit le nombre de job concurente.
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
                // Deserialize le message
                // Utilisation de la classe créer pour sérializer ...
                ContentTypeValidation message = JsonSerializer.Deserialize<ContentTypeValidation>(args.Message.Body.ToString())!;

                if (message == null)
                {
                    throw new InvalidOperationException("Message deserialization failed.");
                }

                _logger.LogInformation($"Processing message: {args.Message.MessageId}, PostId : {message.PostId}, ContentType : {message.ContentType}");

                bool Approved;
                Uri uri;

                switch (message.ContentType)
                {
                    case MVC.Models.ContentType.Text:
                        Approved = await ProcessTextValidationAsync(message.Content);
                        await UpdateCommentDatabaseAsync(message.CommentId, message.PostId, Approved);
                        break;

                    case MVC.Models.ContentType.Image:
                        using (MemoryStream ms = new MemoryStream())
                        {
                            Approved = await ProcessImageValidationAsync(message.Content, ms);
                            uri = await UploadImageAsync(message.Content, ms);
                        }
                        await UpdatePostDatabaseAsync(message.PostId, uri, Approved);
                        await DeleteImageAsync(message.Content);
                        break;
                }

                // Complete the message
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
            var blob = _blobServiceClient.GetBlobContainerClient(_options.BlobContainer1).GetBlockBlobClient(imageId.ToString());
            await blob.DeleteAsync();
        }

        private async Task UpdateCommentDatabaseAsync(Guid commentId, Guid postId, bool Approved)
        {
            try
            {
                var response = await _containerComments.ReadItemAsync<dynamic>(commentId.ToString(), new PartitionKey(postId.ToString()));
                var item = response.Resource;

                item.IsApproved = Approved;

                await _containerComments.ReplaceItemAsync(item, commentId.ToString(), new PartitionKey(postId.ToString()));
                _logger.LogInformation($"Item with ID: {postId} updated successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating item in database.");
                throw;
            }
        }

        private async Task UpdatePostDatabaseAsync(Guid postId, Uri blobUri, bool Approved)
        {
            try
            {
                var response = await _containerPosts.ReadItemAsync<dynamic>(postId.ToString(), new PartitionKey(postId.ToString()));
                var item = response.Resource;

                item.Url = blobUri.ToString();
                item.IsApproved = Approved;

                await _containerPosts.ReplaceItemAsync(item, postId.ToString(), new PartitionKey(postId.ToString()));
                _logger.LogInformation($"Item with ID: {postId} updated successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating item in database.");
                throw;
            }
        }

        private async Task<Uri> UploadImageAsync(string imageId, MemoryStream ms)
        {
            var blob2 = _blobServiceClient.GetBlobContainerClient(_options.BlobContainer2).GetBlockBlobClient(imageId.ToString());
            ms.Position = 0;
            await blob2.UploadAsync(ms);
            return blob2.Uri;
        }

        private async Task<bool> ProcessTextValidationAsync(string Text)
        {
            AnalyzeTextOptions request = new AnalyzeTextOptions(Text);

            Azure.Response<AnalyzeTextResult> response;
            try
            {
                response = await _contentSafetyClient.AnalyzeTextAsync(request);
                _logger.LogError("\nAnalyze text succeeded:");
                return CheckTextSeverity(response.Value.CategoriesAnalysis, TextCategory.Hate.ToString(), TextCategory.SelfHarm.ToString(), TextCategory.Sexual.ToString(), TextCategory.Violence.ToString()); 
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError("Analyze text failed.\nStatus code: {0}, Error code: {1}, Error message: {2}", ex.Status, ex.ErrorCode, ex.Message);
                throw;
            }
        }

        private bool CheckTextSeverity(IReadOnlyList<TextCategoriesAnalysis> analysis, params string[] categories)
        {
            foreach (var category in categories)
            {
                int severity = analysis.FirstOrDefault(a => a.Category == category)?.Severity ?? 0;
                _logger.LogInformation($"{category} severity: {severity}");
                if (severity > 0)
                {
                    return true;
                }
            }
            _logger.LogInformation($"Severity check completed.");
            return false;
        }

        private async Task<bool> ProcessImageValidationAsync(string imageId, MemoryStream ms)
        {
            var blob = _blobServiceClient.GetBlobContainerClient(_options.BlobContainer1).GetBlockBlobClient(imageId.ToString());
            await blob.DownloadToAsync(ms);
            ms.Position = 0;

            // Ont pourrait aussi directement passé un BlobUri
            ContentSafetyImageData image = new ContentSafetyImageData(BinaryData.FromStream(ms));
            AnalyzeImageOptions request = new AnalyzeImageOptions(image);

            Azure.Response<AnalyzeImageResult> response;
            try
            {
                response = await _contentSafetyClient.AnalyzeImageAsync(request);
                _logger.LogInformation("\nAnalyze image succeeded:");
                return CheckImageSeverity(response.Value.CategoriesAnalysis, ImageCategory.Hate.ToString(), ImageCategory.SelfHarm.ToString(), ImageCategory.Sexual.ToString(), ImageCategory.Violence.ToString());
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError("Analyze image failed.\nStatus code: {0}, Error code: {1}, Error message: {2}", ex.Status, ex.ErrorCode, ex.Message);
                throw;
            }
        }

        private bool CheckImageSeverity(IReadOnlyList<ImageCategoriesAnalysis> analysis, params string[] categories)
        {
            foreach (var category in categories)
            {
                int severity = analysis.FirstOrDefault(a => a.Category == category)?.Severity ?? 0;
                _logger.LogError($"{category} severity: {severity}");
                if (severity > 0)
                {
                    return true;
                }
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
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                }
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
