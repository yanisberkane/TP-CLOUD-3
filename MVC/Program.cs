using Microsoft.EntityFrameworkCore;
using Azure.Identity;
using Microsoft.FeatureManagement;

// Project
using MVC.Data;
using MVC.Business;
using MVC.Models;

// Identity
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

// Monitoring
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.ApplicationInsights;
using Microsoft.ApplicationInsights.DependencyCollector;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Cette information pourrait �tre passer via une variable d'environement ou encore mieux, utiliser le endpoint et le defaultazurecredential comme dans l'exemple plus bas.
// https://learn.microsoft.com/en-us/azure/azure-app-configuration/quickstart-aspnet-core-app?tabs=entra-id
// Meilleur option ici en utilisant Microsoft EntraID pour ce connecter via l'endpoint ainsi nous n'avons aucun secrets "exposed"
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
    // Ajout du defaultAzureCredentialOptions pour obtenir les Cred passer dans le docker.
    options.Connect(new Uri(AppConfigEndPoint), defaultAzureCredential)

    // Ajout de la configuration du sentinel pour rafraichir la configuration si il y a changement
    // https://learn.microsoft.com/en-us/azure/azure-app-configuration/enable-dynamic-configuration-aspnet-core
    .Select("*")

    // Requis pour l'ajout des Feature Flag ...
    // https://learn.microsoft.com/en-us/azure/azure-app-configuration/use-feature-flags-dotnet-core
    .UseFeatureFlags()

    .ConfigureRefresh(refreshOptions =>
    refreshOptions.Register("ApplicationConfiguration:Sentinel", refreshAll: true)
        .SetRefreshInterval(new TimeSpan(0, 0, 30)));

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

// Application Insight Service & OpenTelemetry
// https://learn.microsoft.com/en-us/azure/azure-monitor/app/asp-net-core
builder.Services.AddSingleton<ITelemetryInitializer>(new CustomTelemetryInitializer("MVC", Environment.GetEnvironmentVariable("HOSTNAME")!));
builder.Services.AddLogging(logging =>
{
    //logging.ClearProviders();
    logging.AddApplicationInsights(
        configureTelemetryConfiguration: (config) =>
        config.ConnectionString = builder.Configuration.GetConnectionString("ApplicationInsight")!,
        configureApplicationInsightsLoggerOptions: (options) => { }
        );
    logging.AddFilter<ApplicationInsightsLoggerProvider>("MVC", LogLevel.Trace);
});

builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("ApplicationInsight")!;
});

builder.Services.ConfigureTelemetryModule<DependencyTrackingTelemetryModule>((module, o) =>
{
    module.EnableSqlCommandTextInstrumentation = true;
    o.ConnectionString = builder.Configuration.GetConnectionString("ApplicationInsight")!;
});


// Ajouter la BD ( SQL ou NoSQL )
switch (builder.Configuration.GetValue<string>("DatabaseConfiguration"))
{
    case "SQL":
        builder.Services.AddDbContext<ApplicationDbContextSQL>();
        builder.Services.AddScoped<IRepository, EFRepositorySQL>();
        break;

    case "NoSQL":
        builder.Services.AddDbContext<ApplicationDbContextNoSQL>();
        builder.Services.AddScoped<IRepository, EFRepositoryNoSQL>();
        break;

    case "InMemory":
        builder.Services.AddDbContext<ApplicationDbContextInMemory>();
        builder.Services.AddScoped<IRepository, EFRepositoryInMemory>();
        break;
}

// Ajouter le BlobController du BusinessLayer dans nos Injection de d�pendance
builder.Services.AddScoped<BlobController>();

// Ajouter le ServiceBusController du BsinessLayer dans nos Injection ..
builder.Services.AddScoped<ServiceBusController>();

// Service d'identité avec AzureAD
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddControllersWithViews(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));
});
builder.Services.AddRazorPages().AddMicrosoftIdentityUI();

// Add health checks services
builder.Services.AddHealthChecks();

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
            await context.Database.EnsureCreatedAsync();
        }
        break;
}

// Utilise le middleware de AppConfig pour rafraichir la configuration dynamique.
app.UseAzureAppConfiguration();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();


// Identity
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Map health checks endpoint
app.MapHealthChecks("/healthz");

app.MapRazorPages();

app.Run();

public partial class Program { }