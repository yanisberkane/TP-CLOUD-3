using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;
using Worker_Image.Services;
using SharedEvents.Events;

namespace Worker_Image;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly WorkerOptions _options;
    private readonly EventHubService _eventHubService;

    private BlobContainerClient _containerSource;
    private BlobContainerClient _containerTarget;

    public Worker(ILogger<Worker> logger, IOptions<WorkerOptions> options, EventHubService eventHubService)
    {
        _logger = logger;
        _options = options.Value;
        _eventHubService = eventHubService;

        _containerSource = new BlobContainerClient(_options.BlobStorageKey, _options.BlobContainer1); // Unvalidated
        _containerTarget = new BlobContainerClient(_options.BlobStorageKey, _options.BlobContainer2); // Validated
    }

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.LogInformation("Worker_Image started.");

    var inboxPath = "inbox";

    // Ensure the folder exists
    if (!Directory.Exists(inboxPath))
    {
        Directory.CreateDirectory(inboxPath);
        _logger.LogWarning("Inbox folder did not exist. Created at path: {InboxPath}", inboxPath);
    }

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(inboxPath, "*.json"))
            {
                string content = await File.ReadAllTextAsync(filePath);
                var payload = JsonSerializer.Deserialize<ImageResizedEvent>(content);

                if (payload != null)
                {
                    await ResizeAndUploadAsync(payload.PostId);
                    File.Delete(filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing images");
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
    }
}


    private async Task ResizeAndUploadAsync(Guid imageId)
    {
        _logger.LogInformation("Resizing image {ImageId}", imageId);

        BlobClient blobSource = _containerSource.GetBlobClient($"{imageId}.jpg");
        BlobClient blobTarget = _containerTarget.GetBlobClient($"{imageId}.jpg");

        if (!await blobSource.ExistsAsync())
        {
            _logger.LogWarning("Image not found in unvalidated blob: {ImageId}", imageId);
            return;
        }

        using var sourceStream = new MemoryStream();
        await blobSource.DownloadToAsync(sourceStream);
        sourceStream.Position = 0;

        using var image = Image.Load(sourceStream);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(300, 300),
            Mode = ResizeMode.Crop
        }));

        var encoder = new JpegEncoder { Quality = 75 };
        using var outputStream = new MemoryStream();
        image.Save(outputStream, encoder);
        outputStream.Position = 0;

        await blobTarget.UploadAsync(outputStream, overwrite: true);
        _logger.LogInformation("Image resized and uploaded to validated blob: {ImageId}", imageId);

        // Send event to Event Hub
        var evt = new ImageResizedEvent
        {
            PostId = imageId,
            BlobImage = imageId,
            ResizedUrl = blobTarget.Uri.ToString()
        };

        await _eventHubService.SendEventAsync(evt);
        _logger.LogInformation("ImageResizedEvent sent for ImageId: {ImageId}", imageId);
    }
}
