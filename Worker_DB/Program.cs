using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Worker_DB;
using Worker_DB.Services;

var builder = Host.CreateApplicationBuilder(args);

// Register CosmosDB settings
var config = builder.Configuration.GetSection("CosmosDb");
string account = config["Account"] ?? throw new InvalidOperationException("CosmosDb Account configuration is missing.");
string key = config["Key"] ?? throw new InvalidOperationException("CosmosDb Key configuration is missing.");
string databaseName = config["DatabaseName"] ?? throw new InvalidOperationException("CosmosDb DatabaseName configuration is missing.");
string containerName = config["ContainerName"] ?? throw new InvalidOperationException("CosmosDb ContainerName configuration is missing.");

builder.Services.AddSingleton(new CosmosClient(account, key));
builder.Services.AddSingleton(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    return new CosmosDbService(cosmosClient, databaseName, containerName);
});

// Register your background worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

Console.WriteLine("Worker_DB is running... Press Ctrl+C to exit.");
