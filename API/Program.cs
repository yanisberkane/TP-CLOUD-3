using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using System.Reflection;

// Business
using MVC.Models;
using MVC.Data;
using MVC.Business;

// Monitoring
using Microsoft.AspNetCore.Http.HttpResults;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Lecture du AppConfig Endpoint
string AppConfigEndPoint = builder.Configuration.GetValue<string>("Endpoints:AppConfiguration")!;

// Option pour le credential recu des variables d'environement.
DefaultAzureCredential defaultAzureCredential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeSharedTokenCacheCredential = true,
    ExcludeVisualStudioCredential = true,
    ExcludeVisualStudioCodeCredential = true,
    ExcludeEnvironmentCredential = false
});

// Initialize AppConfig
builder.Configuration.AddAzureAppConfiguration(options =>
{
    // Besoin du "App Configuration Data Reader" role
    options.Connect(new Uri(AppConfigEndPoint), defaultAzureCredential);

    options.ConfigureKeyVault(keyVaultOptions =>
    {
        // Besoin du "Key Vault Secrets Officer" role
        keyVaultOptions.SetCredential(defaultAzureCredential);
    });
});

// Ajout du service middleware pour AppConfig et FeatureFlag
builder.Services.AddAzureAppConfiguration();
builder.Services.AddFeatureManagement();

// Liaison de la Configuration "ApplicationConfiguration" a la class
builder.Services.Configure<ApplicationConfiguration>(builder.Configuration.GetSection("ApplicationConfiguration"));

// Add Application Insights telemetry
// I don't like this ...
// Environment.GetEnvironmentVariable("HOSTNAME")!
builder.Services.AddOpenTelemetry().UseAzureMonitor(options => 
{ 
    options.ConnectionString = builder.Configuration.GetConnectionString("ApplicationInsight"); 
    options.EnableLiveMetrics = true;
});

//Add DbContext
// Ajouter la BD ( SQL ou NoSQL )
switch (builder.Configuration.GetValue<string>("DatabaseConfiguration"))
{
    case "SQL":
        builder.Services.AddDbContext<ApplicationDbContextSQL>();
        builder.Services.AddScoped<IRepositoryAPI, EFRepositoryAPISQL>();
        break;

    case "NoSQL":
        builder.Services.AddDbContext<ApplicationDbContextNoSQL>();
        builder.Services.AddScoped<IRepositoryAPI, EFRepositoryAPINoSQL>();
        break;

    case "InMemory":
        builder.Services.AddDbContext<ApplicationDbContextInMemory>();
        builder.Services.AddScoped<IRepositoryAPI, EFRepositoryAPIInMemory>();
        break;
}
// Ajouter le service pour Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<FileUploadOperationFilter>(); // Add custom operation filter, Ceci est pour le FileUpload
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml")); // Pour ajouter la documentation Swagger
});


// Ajouter le BlobController du BusinessLayer dans nos Injection de dépendance
builder.Services.AddScoped<BlobController>();

// Ajouter le ServiceBusController du BsinessLayer dans nos Injection ..
builder.Services.AddScoped<ServiceBusController>();

var app = builder.Build();

// Configuration de la BD ( SQL ou NoSQL )
switch (builder.Configuration.GetValue<string>("DatabaseConfiguration"))
{
    case "SQL":
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContextSQL>();

            dbContext.Database.EnsureDeleted();
            dbContext.Database.Migrate();
        }
        break;

    case "NoSQL":
        using (var scope = app.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContextNoSQL>();
            //await context.Database.EnsureCreatedAsync();
        }
        break;
}

// Configuration des services Swagger
app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

//API Specific
//Post
app.MapGet("/Posts/", async (IRepositoryAPI repo) => await repo.GetAPIPostsIndex());
app.MapGet("/Posts/{id}", async (IRepositoryAPI repo, Guid id) => await repo.GetAPIPost(id));

// https://andrewlock.net/reading-json-and-binary-data-from-multipart-form-data-sections-in-aspnetcore/
// J'ai laisser cette fonction la car je voulais m'assurer de la séparation des concern, sinon j'aurais ajouté de la logique business dans le data layer.
app.MapPost("/Posts/Add", async (IRepositoryAPI repo,  IFormFile Image, HttpRequest request, BlobController blob, ServiceBusController sb) =>
{
    try
    {
        PostCreateDTO post = new PostCreateDTO(request.Form["Title"]!, request.Form["Category"]!, request.Form["User"]!, Image);
        Guid guid = Guid.NewGuid();
        string Url = await blob.PushImageToBlob(post.Image!, guid);
        Post Post = new Post { Title = post.Title!, Category = post.Category, User = post.User!, BlobImage = guid, Url = Url };

        // Sauvegarde du post pour avoir l'Id
        var Result = await repo.CreateAPIPost(Post);

        // Si créer, envoyer dans les services bus
        if (Result.Result is Created<PostReadDTO> CreatedResult)
        {
            PostReadDTO postReadDTO = CreatedResult.Value!;

            // Envoie des messages dans le Service Bus
            await sb.SendImageToResize((Guid)Post.BlobImage!, postReadDTO.Id);
            await sb.SendContentImageToValidation((Guid)Post.BlobImage!, Guid.NewGuid(), postReadDTO.Id);
        }

        return Result;
    }
    catch (ExceptionFilesize)
    {
        return TypedResults.BadRequest();
    }
    // DisableAntiforgery car .net 9.0 l'ajoute automatiquement.
}).DisableAntiforgery();

app.MapPost("/Posts/IncrementPostLike/{id}", async (IRepositoryAPI repo, Guid id) => await repo.APIIncrementPostLike(id));
app.MapPost("/Posts/IncrementPostDislike/{id}", async (IRepositoryAPI repo, Guid id) => await repo.APIIncrementPostDislike(id));

//Comment
//Id or PostId ( va retourner 1 ou plusieurs comments)
app.MapGet("/Comments/{id}", async (IRepositoryAPI repo, Guid id) => await repo.GetAPIComment(id));
app.MapPost("/Comments/Add", async (IRepositoryAPI repo, CommentCreateDTO commentDTO, ServiceBusController sb) =>
{
    var Result = await repo.CreateAPIComment(commentDTO);

    // Si créer, envoyer dans les services bus
    if (Result.Result is Created<CommentReadDTO> CreatedResult)
    {
        CommentReadDTO commentReadDTO = CreatedResult.Value!;

        // Envoie du message dans le Service Bus
        await sb.SendContentTextToValidation(commentReadDTO.Commentaire, commentReadDTO.Id, commentReadDTO.PostId);
    }

    return Result;
});
app.MapPost("/Comments/IncrementCommentLike/{id}", async (IRepositoryAPI repo, Guid id) => await repo.APIIncrementCommentLike(id));
app.MapPost("/Comments/IncrementCommentsDislike/{id}", async (IRepositoryAPI repo, Guid id) => await repo.APIIncrementCommentDislike(id));

app.Run();

// Pour les xUnit test ...
public partial class Program { }