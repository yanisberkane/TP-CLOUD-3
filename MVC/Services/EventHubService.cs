using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using SharedEvents.Events;
using System.Text.Json;
using System.Text;

namespace MVC.Services;

public class EventHubService
{
    private readonly EventHubProducerClient _producerClient;

    public EventHubService(IConfiguration config)
    {
        var connectionString = config.GetSection("EventHub:ConnectionString").Value;
        var eventHubName = config.GetSection("EventHub:Name").Value;

        _producerClient = new EventHubProducerClient(connectionString, eventHubName);
    }

    public async Task SendEventAsync<T>(T eventObject)
    {
        var json = JsonSerializer.Serialize(eventObject);
        using EventDataBatch eventBatch = await _producerClient.CreateBatchAsync();
        eventBatch.TryAdd(new EventData(Encoding.UTF8.GetBytes(json)));
        await _producerClient.SendAsync(eventBatch);
    }
}
