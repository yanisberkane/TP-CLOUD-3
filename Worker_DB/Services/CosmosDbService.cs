using Microsoft.Azure.Cosmos;
using SharedEvents.Events;
using System.Threading.Tasks;

namespace Worker_DB.Services
{
    public class CosmosDbService
    {
        private readonly Container _container;

        public CosmosDbService(CosmosClient dbClient, string databaseName, string containerName)
        {
            _container = dbClient.GetContainer(databaseName, containerName);
        }

        public async Task SavePostAsync(SavePostToDbEvent post)
        {
            await _container.UpsertItemAsync(post, new PartitionKey(post.Category));
        }

        public async Task SaveCommentAsync(SaveCommentToDbEvent comment)
        {
            await _container.UpsertItemAsync(comment, new PartitionKey(comment.PostId.ToString()));
        }

        public async Task SaveContentValidationAsync(ContentValidatedEvent validation)
        {
            await _container.UpsertItemAsync(validation, new PartitionKey(validation.PostId.ToString()));
        }

        public async Task SaveImageResizedAsync(ImageResizedEvent imageResized)
        {
            await _container.UpsertItemAsync(imageResized, new PartitionKey(imageResized.PostId.ToString()));
        }
    }
}
