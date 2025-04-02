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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

//Json
using System.Text.Json;

namespace Worker_Image
{
    public class Worker : BackgroundService
    {
        private ILogger<Worker> _logger;
        private ServiceBusClient _serviceBusClient;
        private ServiceBusProcessor _processor;
        private ConcurrentQueue<ServiceBusReceivedMessage> _messageQueue;
        private WorkerOptions _options;
        private BlobServiceClient _blobServiceClient;
        private SemaphoreSlim _semaphore;

        private const int ConcurentJobLimit = 5;
        private const int ProcessingDelayMS = 30000;        //Delais pour ralentir le traitement pour load testing.

        /// <summary>
        /// Constructeur, il initialize tout les services qui seront utiliser de facon asynchrone par nos functions.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        public Worker(ILogger<Worker> logger, IOptions<WorkerOptions> options)
        {
            _logger = logger;
            _options = options.Value;

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
            string queueName = "imageresizemessage";

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

        /// <summary>
        /// Fonctions activé lors de la réceptions d'un message, ils sont mit en queue, la semaphore sert a synchroniser le nombre maximum de tache.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            await _semaphore.WaitAsync();
            _messageQueue.Enqueue(args.Message);

            _ = ProcessMessagesAsync(args);
        }

        /// <summary>
        /// Fonction de gestion des messages
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task ProcessMessagesAsync(ProcessMessageEventArgs args)
        {
            try
            {
                // Deserialize the message
                // Nous avions passé un Tuple ici lors de la sérialization ...
                // Guid ImageId, Guid Id
                var message = JsonSerializer.Deserialize<Tuple<Guid, Guid>>(args.Message.Body.ToString());
                if (message == null)
                {
                    throw new InvalidOperationException("Message deserialization failed.");
                }

                _logger.LogInformation($"Processing message: {args.Message.MessageId}, PostId : {message.Item2}, Image : {message.Item1}");

                using (MemoryStream ms = new MemoryStream())
                {
                    try
                    {
                        await ProcessImageAsync(message.Item1, ms);

                        // Complete the message
                        await args.CompleteMessageAsync(args.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing image for message: {args.Message.MessageId}");
                        await HandleMessageProcessingErrorAsync(args);
                    }
                }
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

        /// <summary>
        /// Fonction pour Recevoir l'images du Blob et la redimenssionner
        /// </summary>
        /// <param name="imageId"></param>
        /// <param name="ms"></param>
        /// <returns></returns>
        private async Task ProcessImageAsync(Guid imageId, MemoryStream ms)
        {
            // Valide sur quel blob est l'image a redimenssionner.
            // Retourne True si sur le container1, sinon il va etre sur le 2
            bool Container1 = await _blobServiceClient.GetBlobContainerClient(_options.BlobContainer1).GetBlobClient(imageId.ToString()).ExistsAsync();

            // Si Container1 = true, utilise _options.BlobContainer1, sinon _options.BlobContainer2
            // Container1 ? _options.BlobContainer1 : _options.BlobContainer2
            var blob = _blobServiceClient.GetBlobContainerClient(Container1 ? _options.BlobContainer1 : _options.BlobContainer2).GetBlockBlobClient(imageId.ToString());
            await blob.DownloadToAsync(ms);
            ms.Position = 0;

            // https://docs.sixlabors.com/articles/imagesharp/resize.html
            // If you pass 0 as any of the values for width and height dimensions then ImageSharp will automatically determine the correct opposite dimensions size to preserve the original aspect ratio.
            using (var image = Image.Load(ms))
            {
                image.Mutate(c => c.Resize(500, 0));
                ms.Position = 0;
                await image.SaveAsPngAsync(ms);
                ms.Position = 0;
            }

            await Task.Delay(ProcessingDelayMS);

            //Upload l'image sur le même container.
            await blob.UploadAsync(ms);
        }

        /// <summary>
        /// Fonction de gestion d'erreur
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task HandleMessageProcessingErrorAsync(ProcessMessageEventArgs args)
        {
            if (args.Message.DeliveryCount > 5)
            {
                await args.DeadLetterMessageAsync(args.Message, "Image Processing Errror", "Exceed Maximum retries");
            }
            else
            {
                await args.AbandonMessageAsync(args.Message);
            }
        }

        /// <summary>
        /// Seconde fonction de gestion d'erreur
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
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

        /// <summary>
        /// Fonction principale de loop interne.
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
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
