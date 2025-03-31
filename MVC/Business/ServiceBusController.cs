using MVC.Models;
using Microsoft.Extensions.Options;
using Azure.Messaging.ServiceBus;
using System.Text.Json;

namespace MVC.Business
{
    public class ServiceBusController
    {
        private ApplicationConfiguration _applicationConfiguration { get; }
        private ServiceBusClientOptions _serviceBusClientOptions { get; }

        public ServiceBusController(IOptionsSnapshot<ApplicationConfiguration> options)
        {
            _applicationConfiguration = options.Value;

            // Set the transport type to AmqpWebSockets so that the ServiceBusClient uses the port 443. 
            // If you use the default AmqpTcp, ensure that ports 5671 and 5672 are open.
            // Service Bus Retry options
            // https://learn.microsoft.com/en-us/azure/architecture/best-practices/retry-service-specific

            _serviceBusClientOptions = new ServiceBusClientOptions
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
        }

        private async Task SendMessageAsync(string queueName, ServiceBusMessage message, int Defer = 0)
        {
            await using ServiceBusClient serviceBusClient = new ServiceBusClient(_applicationConfiguration.ServiceBusConnectionString, _serviceBusClientOptions);
            ServiceBusSender serviceBusSender = serviceBusClient.CreateSender(queueName);

            if (Defer != 0)
            {
                DateTimeOffset scheduleTime = DateTimeOffset.UtcNow.AddMinutes(5);
                await serviceBusSender.ScheduleMessageAsync(message, scheduleTime);
            }
            else 
                await serviceBusSender.SendMessageAsync(message);
        }

        public async Task SendImageToResize(Guid imageName, Guid Id)
        {
            Console.WriteLine("Envoi d'un message pour ImageResize : " + DateTime.Now.ToString());
            ServiceBusMessage message = new ServiceBusMessage(JsonSerializer.Serialize(new Tuple<Guid,Guid> (imageName,Id)));
            await SendMessageAsync(_applicationConfiguration.SB_resizeQueueName, message);
        }

        public async Task SendContentTextToValidation(string text, Guid CommentId, Guid PostId)
        {
            Console.WriteLine("Envoi d'un message pour Text Content Validation : " + DateTime.Now.ToString());
            ServiceBusMessage message = new ServiceBusMessage(JsonSerializer.Serialize(new ContentTypeValidation(ContentType.Text, text, CommentId, PostId)));
            await SendMessageAsync(_applicationConfiguration.SB_contentQueueName, message);
        }

        public async Task SendContentImageToValidation(Guid imageName, Guid CommentId, Guid PostId)
        {
            Console.WriteLine("Envoi d'un message pour Image Content Validation : " + DateTime.Now.ToString());
            ServiceBusMessage message = new ServiceBusMessage(JsonSerializer.Serialize(new ContentTypeValidation(ContentType.Image, imageName.ToString(), CommentId, PostId)));

            // Messsage planifier dans 5 minutes, le but étant de laisser le temps au Resize de passé avant.
            // Ceci n'est vraiment pas un design idéal.

            await SendMessageAsync(_applicationConfiguration.SB_contentQueueName, message, 1);
        }
    }
}
