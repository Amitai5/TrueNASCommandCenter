using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Notifications;
using TrueNasUpdateManager.Services;

namespace TrueNasUpdateManager.Tests;

[TestClass]
public sealed class PersistenceAndNotificationTests
{
    [TestMethod]
    public async Task MigrationAndRepository_PersistUnconfiguredAppsAndEncryptedSecrets()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = database.CreateSettingsService();
        var model = await service.GetFormAsync();
        model.TrueNasUrl = "wss://server.test/api/current";
        model.TrueNasUsername = "service";
        model.NewTrueNasApiKey = "plain-api-secret";

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
        Assert.IsNull(form.NewTrueNasApiKey);
        Assert.IsTrue(form.HasSavedTrueNasApiKey);
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
