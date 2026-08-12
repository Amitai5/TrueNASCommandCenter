using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Components;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Integrations.TrueNas;
using TrueNasUpdateManager.Notifications;
using TrueNasUpdateManager.Scheduling;
using TrueNasUpdateManager.Services;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["DATA_PATH"];
if (string.IsNullOrWhiteSpace(dataPath))
{
    dataPath = "/data";
}

var databasePath = Path.Combine(dataPath, "app.db");
var connectionString = $"Data Source={databasePath};Cache=Shared;Foreign Keys=True;Pooling=True";

builder.Services.AddSingleton(new DataPathOptions(dataPath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InitializationState>();
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
builder.Services.AddSingleton<ITrueNasClient, TrueNasJsonRpcClient>();
builder.Services.AddSingleton<RunLock>();
builder.Services.AddSingleton<IVersionClassifier, VersionClassifier>();
builder.Services.AddSingleton<IUpdatePolicyEvaluator, UpdatePolicyEvaluator>();
builder.Services.AddScoped<IAppDiscoveryService, AppDiscoveryService>();
builder.Services.AddScoped<IUpdateExecutor, UpdateExecutor>();
builder.Services.AddScoped<IUpdateCoordinator, UpdateCoordinator>();
builder.Services.AddScoped<IEmailNotificationSender, EmailNotificationSender>();
builder.Services.AddScoped<IWebhookNotificationSender, WebhookNotificationSender>();
builder.Services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
builder.Services.AddSingleton<IScheduleService, ScheduleService>();
builder.Services.AddHttpClient("webhook");
builder.Services.AddHostedService<RunSchedulerBackgroundService>();
builder.Services.AddHealthChecks()
    .AddCheck("live", static () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), ["live"])
    .AddCheck<DatabaseReadyHealthCheck>("ready", tags: ["ready"]);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<InitializationState>().IsReady = true;

await app.RunAsync();

public partial class Program;
