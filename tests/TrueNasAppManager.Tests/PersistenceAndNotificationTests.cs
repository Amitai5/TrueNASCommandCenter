using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Notifications;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class PersistenceAndNotificationTests
{
    [TestMethod]
    public async Task AppDbContext_ConcurrentWritesAreSerialized()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var writes = Enumerable.Range(0, 20).Select(async index =>
        {
            await using var db = await database.CreateDbContextAsync();
            db.UpdateRuns.Add(new UpdateRun
            {
                Trigger = RunTrigger.RefreshApps,
                StartedUtc = new DateTime(2026, 8, 22, 12, index, 0, DateTimeKind.Utc),
                Status = RunStatus.Succeeded
            });
            await db.SaveChangesAsync();
        });

        await Task.WhenAll(writes);

        await using var verificationDb = await database.CreateDbContextAsync();
        Assert.AreEqual(20, await verificationDb.UpdateRuns.CountAsync());
    }

    [TestMethod]
    public async Task Migration_MapsLegacyDowntimeChoiceAndKeepsExistingAppsInstalled()
    {
        await using var database = new TestDatabase();
        await using (var legacy = await database.CreateDbContextAsync())
        {
            await legacy.Database.MigrateAsync("20260822180000_AddAppDowntimeMonitoring");
            await legacy.Database.ExecuteSqlRawAsync("INSERT INTO Apps (Id, Name, IsCustom, State, CatalogUpdateAvailable, ImageUpdateAvailable, ActionRequired, LastSeenUtc, VersionScope, SnapshotHostPaths, StatusLabel, NotifyOnDowntime, DowntimeNotificationActive) VALUES ('legacy', 'Legacy', 0, 'RUNNING', 0, 0, 0, '2026-08-22 21:00:00', 'AnyVersion', 0, 'Up to date', 1, 0);");
            await legacy.Database.MigrateAsync();
        }

        await using var verification = await database.CreateDbContextAsync();
        var app = await verification.Apps.SingleAsync();

        Assert.AreEqual(DowntimeAction.NotifyOnly, app.DowntimeAction);
        Assert.AreEqual(AppHealthState.Running, app.HealthState);
        Assert.IsTrue(app.IsInstalled);
    }

    [TestMethod]
    public async Task MigrationAndRepository_PersistUnconfiguredAppsAndEncryptedSecrets()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = database.CreateSettingsService();
        var model = await service.GetFormAsync();
        model.TrueNasUsername = "service";
        model.NewTrueNasApiKey = "plain-api-secret";
        model.UptimeKumaEnabled = true;
        model.UptimeKumaBaseUrl = "http://kuma.local:3001";
        model.UptimeKumaBrowserUrl = "https://status.example.test";
        model.NewUptimeKumaApiKey = "plain-kuma-secret";
        model.UptimeKumaRefreshIntervalSeconds = 90;

        await service.SaveAsync(model);
        await using (var db = await database.CreateDbContextAsync())
        {
            db.Apps.Add(new AppRecord
            {
                Id = "new-app",
                Name = "New app",
                State = "RUNNING",
                LastSeenUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var verification = await database.CreateDbContextAsync();
        var app = await verification.Apps.SingleAsync();
        var settings = await verification.Settings.SingleAsync();
        var form = await service.GetFormAsync();

        Assert.IsNull(app.Policy);
        Assert.IsNotNull(settings.TrueNasApiKeyEncrypted);
        Assert.DoesNotContain("plain-api-secret", settings.TrueNasApiKeyEncrypted);
        Assert.AreEqual(TestDatabase.TrueNasEndpoint.ServerUrl, settings.TrueNasUrl);
        Assert.IsFalse(settings.AllowInsecureWebSocket);
        Assert.IsNull(form.NewTrueNasApiKey);
        Assert.IsTrue(form.HasSavedTrueNasApiKey);
        Assert.IsTrue(settings.UptimeKumaEnabled);
        Assert.AreEqual("http://kuma.local:3001/", settings.UptimeKumaBaseUrl);
        Assert.AreEqual("https://status.example.test/", settings.UptimeKumaBrowserUrl);
        Assert.DoesNotContain("plain-kuma-secret", settings.UptimeKumaApiKeyEncrypted!);
        Assert.IsTrue(form.HasSavedUptimeKumaApiKey);
        Assert.AreEqual(90, form.UptimeKumaRefreshIntervalSeconds);
    }

    /// <summary>Verifies that legacy database settings cannot override the deployment-configured endpoint.</summary>
    [TestMethod]
    public async Task Settings_ConnectionOptionsIgnoreLegacyEndpointAndUseDeploymentConfiguration()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings =>
        {
            settings.TrueNasUrl = "wss://legacy.example.test/api/current";
            settings.AllowInsecureWebSocket = true;
            settings.TrueNasUsername = "service";
            settings.TrueNasApiKeyEncrypted = protector.Protect("test-api-key");
            settings.VerifyTls = false;
        });
        var service = database.CreateSettingsService();

        var options = await service.GetConnectionOptionsAsync();

        Assert.AreEqual(TestDatabase.TrueNasEndpoint.ServerUri, options.ServerUri);
        Assert.AreEqual("service", options.Username);
        Assert.AreEqual("test-api-key", options.ApiKey);
        Assert.IsFalse(options.VerifyTls);
    }

    [TestMethod]
    public async Task Dispatcher_DeduplicatesSameUnresolvedUpdatePerProvider()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings =>
        {
            settings.EmailEnabled = true;
            settings.NotifyManualApproval = true;
        });
        var email = new FakeEmailSender();
        var dispatcher = new NotificationDispatcher(
            database,
            email,
            new FakeWebhookSender(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero)));
        var notification = Event(
            NotificationEventType.ManualApprovalAvailable,
            "ManualApproval|app|2.0.0|VERSION_SCOPE");

        await dispatcher.DispatchAsync(notification);
        await dispatcher.DispatchAsync(notification with { EventId = Guid.NewGuid() });

        await using var db = await database.CreateDbContextAsync();
        Assert.AreEqual(1, email.Calls);
        Assert.AreEqual(1, await db.Notifications.CountAsync());
        Assert.AreEqual(DeliveryStatus.Delivered, (await db.Notifications.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task Dispatcher_RateLimitsConnectionFailuresAcrossChangedReasons()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings =>
        {
            settings.EmailEnabled = true;
            settings.NotifyConnectionFailure = true;
            settings.ConnectionFailureCooldownMinutes = 60;
        });
        var email = new FakeEmailSender();
        var dispatcher = new NotificationDispatcher(
            database,
            email,
            new FakeWebhookSender(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero)));

        await dispatcher.DispatchAsync(Event(NotificationEventType.TrueNasConnectionFailed, "connection|NETWORK"));
        await dispatcher.DispatchAsync(Event(NotificationEventType.TrueNasConnectionFailed, "connection|TLS"));

        Assert.AreEqual(1, email.Calls);
    }

    [TestMethod]
    public async Task RunLock_RejectsOverlapUntilReleased()
    {
        var runLock = new RunLock();
        await using var first = await runLock.TryAcquireAsync(CancellationToken.None);

        var overlapping = await runLock.TryAcquireAsync(CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.IsNull(overlapping);
        await first.DisposeAsync();
        await using var next = await runLock.TryAcquireAsync(CancellationToken.None);
        Assert.IsNotNull(next);
    }

    private static NotificationEvent Event(NotificationEventType type, string key) =>
        new(
            Guid.NewGuid(),
            type,
            new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc),
            key,
            "Subject",
            "Message",
            "REASON",
            "app",
            "App",
            "1.0.0",
            "2.0.0");
}
