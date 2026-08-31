using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Components;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Integrations.UptimeKuma;
using TrueNasCommandCenter.Notifications;
using TrueNasCommandCenter.Scheduling;
using TrueNasCommandCenter.Services;

const string LegacyDataProtectionApplicationName = "TrueNasUpdateManager";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var trueNasEndpoint = TrueNasEndpointOptions.Parse(builder.Configuration["TRUENAS_WEBSOCKET_URL"]);
var dataPath = builder.Configuration["DATA_PATH"];
if (string.IsNullOrWhiteSpace(dataPath))
{
    dataPath = "/data";
}

var databasePath = Path.Combine(dataPath, "app.db");
var dataProtectionPath = Path.Combine(dataPath, "data-protection");
var connectionString = $"Data Source={databasePath};Cache=Shared;Foreign Keys=True;Pooling=True;Default Timeout=30";

Directory.CreateDirectory(dataProtectionPath);
if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(
        dataProtectionPath,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

builder.Services.AddSingleton(new DataPathOptions(dataPath));
builder.Services.AddSingleton(trueNasEndpoint);
builder.Services.AddDataProtection()
    // Keep the original discriminator so upgrades can decrypt existing credentials and push keys.
    .SetApplicationName(LegacyDataProtectionApplicationName)
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InitializationState>();
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
builder.Services.AddSingleton<ITrueNasServerAddressService, TrueNasServerAddressService>();
builder.Services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
builder.Services.AddSingleton<TrueNasJsonRpcClient>();
builder.Services.AddSingleton<ITrueNasClient>(services => services.GetRequiredService<TrueNasJsonRpcClient>());
builder.Services.AddSingleton<ITrueNasCatalogClient>(services => services.GetRequiredService<TrueNasJsonRpcClient>());
builder.Services.AddSingleton<ITrueNasSystemClient>(services => services.GetRequiredService<TrueNasJsonRpcClient>());
builder.Services.AddSingleton<IStoragePoolOverviewService, StoragePoolOverviewService>();
builder.Services.AddSingleton<ITrueNasSystemOverviewService, TrueNasSystemOverviewService>();
builder.Services.AddScoped<DashboardOverviewService>();
builder.Services.AddScoped<DashboardRefreshService>();
builder.Services.AddSingleton<IOperationsInboxService, OperationsInboxService>();
builder.Services.AddSingleton<AppResourceMonitorService>();
builder.Services.AddSingleton<IAppResourceMonitor>(services => services.GetRequiredService<AppResourceMonitorService>());
builder.Services.AddSingleton<RunLock>();
builder.Services.AddSingleton<IVersionClassifier, VersionClassifier>();
builder.Services.AddSingleton<IUpdatePolicyEvaluator, UpdatePolicyEvaluator>();
builder.Services.AddScoped<IAppDiscoveryService, AppDiscoveryService>();
builder.Services.AddScoped<IAppManagementService, AppManagementService>();
builder.Services.AddScoped<IAppHealthMonitorService, AppHealthMonitorService>();
builder.Services.AddScoped<IUpdateExecutor, UpdateExecutor>();
builder.Services.AddScoped<IUpdateCoordinator, UpdateCoordinator>();
builder.Services.AddSingleton<IAppLinkService, AppLinkService>();
builder.Services.AddSingleton<IGitHubMetadataService, GitHubMetadataService>();
builder.Services.AddSingleton<ICatalogLinkService, CatalogLinkService>();
builder.Services.AddSingleton<ICatalogReadmeSanitizer, CatalogReadmeSanitizer>();
builder.Services.AddSingleton<IActiveDeploymentProvider, TrueNasActiveDeploymentProvider>();
builder.Services.AddSingleton<IAppsMarketMetadataProvider, TrueNasAppsMarketMetadataProvider>();
builder.Services.AddSingleton<ICatalogDiscoveryService, CatalogDiscoveryService>();
builder.Services.AddSingleton<IDockerHubDiscoveryService, DockerHubDiscoveryService>();
builder.Services.AddSingleton<UptimeKumaMetricsParser>();
builder.Services.AddSingleton<IUptimeKumaClient, UptimeKumaClient>();
builder.Services.AddSingleton<IUptimeKumaSyncService, UptimeKumaSyncService>();
builder.Services.AddScoped<IEmailNotificationSender, EmailNotificationSender>();
builder.Services.AddScoped<IWebhookNotificationSender, WebhookNotificationSender>();
builder.Services.AddSingleton<IWebPushSubscriptionService, WebPushSubscriptionService>();
builder.Services.AddSingleton<IWebPushProtocolClient, WebPushProtocolClient>();
builder.Services.AddScoped<IWebPushNotificationSender, WebPushNotificationSender>();
builder.Services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
builder.Services.AddSingleton<IScheduleService, ScheduleService>();
builder.Services.AddSingleton<IConfigurationBackupService, ConfigurationBackupService>();
builder.Services.AddHttpClient("webhook");
builder.Services.AddHttpClient("web-push", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("github", client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("truenas-catalog-telemetry", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("truenas-apps-market", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("docker-hub", client =>
{
    client.BaseAddress = new Uri("https://hub.docker.com/");
    client.Timeout = TimeSpan.FromSeconds(12);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("uptime-kuma", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("uptime-kuma-insecure", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
builder.Services.AddHostedService<RunSchedulerBackgroundService>();
builder.Services.AddHostedService<UptimeKumaSyncBackgroundService>();
builder.Services.AddHostedService<OperationsInboxBackgroundService>();
builder.Services.AddHostedService(services => services.GetRequiredService<AppResourceMonitorService>());
builder.Services.AddHealthChecks()
    .AddCheck("live", static () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), ["live"])
    .AddCheck<DatabaseReadyHealthCheck>("ready", tags: ["ready"]);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
app.Logger.LogInformation("Starting TrueNAS Command Center {ApplicationVersion}", ApplicationVersion.Current);
app.Logger.LogInformation("Persistent ASP.NET Data Protection key ring configured at {DataProtectionPath}", dataProtectionPath);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path.Value;
    var isServiceWorker = string.Equals(requestPath, "/service-worker.js", StringComparison.OrdinalIgnoreCase);
    var isManifest = string.Equals(requestPath, "/manifest.webmanifest", StringComparison.OrdinalIgnoreCase);
    if (isServiceWorker || isManifest)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            if (isServiceWorker)
            {
                context.Response.Headers["Service-Worker-Allowed"] = "/";
                context.Response.ContentType = "text/javascript; charset=utf-8";
            }
            else
            {
                context.Response.ContentType = "application/manifest+json; charset=utf-8";
            }

            return Task.CompletedTask;
        });
    }

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["X-Application-Version"] = ApplicationVersion.Current;
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: https://media.sys.truenas.net https://djeqr6to3dedg.cloudfront.net https://www.gravatar.com; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:; frame-ancestors 'none'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/version", () => Results.Ok(new { Name = "TrueNAS Command Center", Version = ApplicationVersion.Current }));
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<InitializationState>().IsReady = true;

await app.RunAsync();

public partial class Program;
