using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SharedEvents.Events;
using System.Text;
using Worker_DB.Services;

namespace Worker_DB
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly CosmosDbService _dbService;
        private const string consumerGroup = EventHubConsumerClient.DefaultConsumerGroupName;
        private readonly EventHubConsumerClient _consumer;

        public Worker(ILogger<Worker> logger, CosmosDbService dbService, IConfiguration configuration)
        {
            _logger = logger;
            _dbService = dbService;

            var connectionString = configuration["EventHub:ConnectionString"];
            var eventHubName = configuration["EventHub:Name"];
            _consumer = new EventHubConsumerClient(consumerGroup, connectionString, eventHubName);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (PartitionEvent partitionEvent in _consumer.ReadEventsAsync(stoppingToken))
            {
                try
                {
                    var body = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());

                    // SAFELY get event type
                    if (!partitionEvent.Data.Properties.TryGetValue("EventType", out var eventTypeObj))
                    {
                        _logger.LogWarning("EventType property missing. Skipping event.");
                        continue;
                    }

                    var eventType = eventTypeObj?.ToString();

                    switch (eventType)
                    {
                        case nameof(SavePostToDbEvent):
                            var post = JsonConvert.DeserializeObject<SavePostToDbEvent>(body);
                            if (post != null)
                            {
                                await _dbService.SavePostAsync(post);
                                _logger.LogInformation($"Saved post {post.PostId}.");
                            }
                            break;

                        case nameof(SaveCommentToDbEvent):
                            var comment = JsonConvert.DeserializeObject<SaveCommentToDbEvent>(body);
                            if (comment != null)
                            {
                                await _dbService.SaveCommentAsync(comment);
                                _logger.LogInformation($"Saved comment {comment.CommentId}.");
                            }
                            break;

                        case nameof(ContentValidatedEvent):
                            var validation = JsonConvert.DeserializeObject<ContentValidatedEvent>(body);
                            if (validation != null)
                            {
                                await _dbService.SaveContentValidationAsync(validation);
                                _logger.LogInformation($"Saved content validation for Post {validation.PostId}.");
                            }
                            break;

                        default:
                            _logger.LogWarning($"⚠️ Unknown event type: {eventType}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing event.");
                }
            }
        }

    }
}
