using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharedEvents.Events;
using System.Text.Json;
using Worker_DB.Data;
using Worker_DB.Models;

public class EventHubWorker : BackgroundService
{
    private readonly ILogger<EventHubWorker> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;

    public EventHubWorker(ILogger<EventHubWorker> logger, IServiceProvider services, IConfiguration configuration)
    {
        _logger = logger;
        _services = services;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration["EventHub:ConnectionString"];
        var eventHubName = _configuration["EventHub:Name"];
        var blobStorage = _configuration["EventHub:BlobStorageConnectionString"];
        var blobContainer = _configuration["EventHub:BlobContainerName"];

        var processor = new EventProcessorClient(
            new BlobContainerClient(blobStorage, blobContainer),
            EventHubConsumerClient.DefaultConsumerGroupName,
            connectionString,
            eventHubName);

        processor.ProcessEventAsync += ProcessEventHandler;
        processor.ProcessErrorAsync += ErrorHandler;

        await processor.StartProcessingAsync(stoppingToken);
    }

    private async Task ProcessEventHandler(ProcessEventArgs eventArgs)
    {
        var json = eventArgs.Data.EventBody.ToString();
        var eventType = eventArgs.Data.Properties["Type"]?.ToString();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();

        switch (eventType)
        {
            case nameof(PostCreatedEvent):
                var postEvent = JsonSerializer.Deserialize<PostCreatedEvent>(json);
                if (postEvent != null)
                {
                    db.Posts.Add(new Post
                    {
                        Id = postEvent.PostId,
                        Title = postEvent.Title,
                        Category  = Enum.Parse<Category>(postEvent.Category),
                        User = postEvent.User,
                        Created = postEvent.Created,
                        BlobImage = postEvent.BlobImage,
                        Url = postEvent.Url
                    });
                }
                break;

            case nameof(CommentCreatedEvent):
                var commentEvent = JsonSerializer.Deserialize<CommentCreatedEvent>(json);
                if (commentEvent != null)
                {
                    db.Comments.Add(new Comment
                    {
                        Id = commentEvent.CommentId,
                        PostId = commentEvent.PostId,
                        User = commentEvent.User,
                        Created = commentEvent.Created,
                        Commentaire = commentEvent.Content
                    });
                }
                break;
        }

        await db.SaveChangesAsync();
        await eventArgs.UpdateCheckpointAsync(eventArgs.CancellationToken);
    }

    private Task ErrorHandler(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Event Hub processing error.");
        return Task.CompletedTask;
    }
}
