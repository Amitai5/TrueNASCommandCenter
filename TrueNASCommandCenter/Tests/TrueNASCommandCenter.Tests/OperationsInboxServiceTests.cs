using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Notifications;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class OperationsInboxServiceTests
{
    [TestMethod]
    [TestCategory("Regression")]
    public async Task RefreshAsync_AllSourcesAvailable_CreatesUnifiedFeedAndPushesActionableItems()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        await SeedLocalSourcesAsync(database, now.UtcDateTime);
        var client = new FakeOperationsSystemClient
        {
            Alerts =
            [
                new TrueNasAlertDto { Uuid = "alert-1", ClassName = "PoolSpace", Level = "CRITICAL", Text = "Pool Main is nearly full.", CreatedAt = now.AddMinutes(-5), LastOccurrence = now.AddMinutes(-1) }
            ],
            Jobs =
            [
                new TrueNasJobDto { Id = 42, Method = "app.upgrade", Arguments = Json("[\"plex\"]"), Description = "Upgrade Plex", State = "RUNNING", Progress = new TrueNasJobProgressDto { Percent = 45, Description = "Pulling images" }, TimeStarted = Json("\"2026-08-30T17:50:00Z\"") }
            ],
            Pools =
            [
                new TrueNasPoolDto { Name = "Main", Status = "ONLINE", Healthy = true, Scan = new TrueNasPoolScanDto { Function = "SCRUB", State = "SCANNING", StartTime = Json("\"2026-08-30T17:00:00Z\""), Percentage = 20 } }
            ]
        };
        var pushSender = new FakeWebPushSender(hasSubscriptions: true);
        await using var provider = CreateProvider(database, pushSender, now);
        var service = CreateService(database, client, provider, now);

        var result = await service.RefreshAsync();
        var snapshot = await service.GetSnapshotAsync(new OperationsInboxQuery(SinceUtc: now.AddDays(-1).UtcDateTime));

        Assert.AreEqual(6, result.ObservedCount);
        Assert.HasCount(6, snapshot.Items);
        Assert.IsTrue(snapshot.Items.Any(item => item.Kind == OperationsInboxKind.TrueNasAlert && item.DeepLink == "/system#system-alerts"));
        Assert.IsTrue(snapshot.Items.Any(item => item.Kind == OperationsInboxKind.TrueNasJob && item.RelatedAppId == "plex" && item.ProgressPercent == 45));
        Assert.IsTrue(snapshot.Items.Any(item => item.Kind == OperationsInboxKind.PoolScrub && item.DeepLink == "/system#storage-pools"));
        Assert.IsTrue(snapshot.Items.Any(item => item.Kind == OperationsInboxKind.AppUpdateFailure && item.DeepLink.StartsWith("/history?app=", StringComparison.Ordinal)));
        Assert.IsTrue(snapshot.Items.Any(item => item.Kind == OperationsInboxKind.UptimeKumaOutage));
        Assert.IsTrue(snapshot.Items.Any(item => item.Kind == OperationsInboxKind.NotificationFailure));
        Assert.AreEqual(3, pushSender.Calls);
        Assert.IsTrue(snapshot.Items.Where(item => item.Source != OperationsInboxSource.Notifications && item.Severity >= OperationsInboxSeverity.Warning).All(item => item.PushState == OperationsInboxPushState.Delivered));
    }

    /// <summary>Verifies a normal idle pool scan does not make the pool-scan inbox source fail.</summary>
    [TestMethod]
    [TestCategory("Regression")]
    public async Task RefreshAsync_IdlePoolScan_DoesNotReportSourceWarning()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        var client = new FakeOperationsSystemClient
        {
            Pools =
            [
                new TrueNasPoolDto { Name = "Main", Status = "ONLINE", Healthy = true, Scan = new TrueNasPoolScanDto() }
            ]
        };
        await using var provider = CreateProvider(database, new FakeWebPushSender(), now);
        var service = CreateService(database, client, provider, now);

        var result = await service.RefreshAsync();
        var snapshot = await service.GetSnapshotAsync(new OperationsInboxQuery());

        Assert.IsEmpty(result.Warnings);
        Assert.IsFalse(snapshot.Items.Any(item => item.Kind is OperationsInboxKind.PoolScrub or OperationsInboxKind.PoolResilver));
    }

    [TestMethod]
    [TestCategory("Regression")]
    public async Task RefreshAsync_SourceRecoversThenFailsAgain_ResolvesAndReopensOccurrence()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        var alert = new TrueNasAlertDto { Uuid = "repeat-alert", ClassName = "Disk", Level = "WARNING", Text = "Disk temperature high.", CreatedAt = now };
        var client = new FakeOperationsSystemClient { Alerts = [alert] };
        await using var provider = CreateProvider(database, new FakeWebPushSender(), now);
        var service = CreateService(database, client, provider, now);

        await service.RefreshAsync();
        var initial = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(item => item.Kind == OperationsInboxKind.TrueNasAlert);
        Assert.IsTrue(await service.AcknowledgeAsync(initial.Id));

        await service.RefreshAsync();
        var acknowledged = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(item => item.Id == initial.Id);
        Assert.AreEqual(OperationsInboxStatus.Acknowledged, acknowledged.Status);

        client.Alerts = [];
        await service.RefreshAsync();
        var resolved = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(item => item.Id == initial.Id);
        Assert.AreEqual(OperationsInboxStatus.Resolved, resolved.Status);
        Assert.IsFalse(resolved.IsSourceActive);

        client.Alerts = [alert];
        await service.RefreshAsync();
        var reopened = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(item => item.Id == initial.Id);
        var history = await service.GetHistoryAsync(initial.Id);

        Assert.AreEqual(OperationsInboxStatus.Open, reopened.Status);
        Assert.AreEqual(2, reopened.OccurrenceCount);
        Assert.IsTrue(history.Any(entry => entry.Action == OperationsInboxHistoryAction.Acknowledged));
        Assert.IsTrue(history.Any(entry => entry.Action == OperationsInboxHistoryAction.Resolved));
        Assert.IsTrue(history.Any(entry => entry.Action == OperationsInboxHistoryAction.Reopened));
    }

    [TestMethod]
    [TestCategory("Regression")]
    public async Task ResolveAsync_SourceRemainsActive_DoesNotImmediatelyReopen()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        var client = new FakeOperationsSystemClient
        {
            Alerts = [new TrueNasAlertDto { Uuid = "manual-resolution", Level = "ERROR", Text = "Operator is handling this.", CreatedAt = now }]
        };
        await using var provider = CreateProvider(database, new FakeWebPushSender(), now);
        var service = CreateService(database, client, provider, now);
        await service.RefreshAsync();
        var item = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(item => item.Kind == OperationsInboxKind.TrueNasAlert);

        Assert.IsTrue(await service.ResolveAsync(item.Id));
        await service.RefreshAsync();
        var resolved = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(candidate => candidate.Id == item.Id);

        Assert.AreEqual(OperationsInboxStatus.Resolved, resolved.Status);
        Assert.IsTrue(resolved.IsSourceActive);
        Assert.AreEqual(1, resolved.OccurrenceCount);
    }

    [TestMethod]
    [TestCategory("Regression")]
    public async Task RefreshAsync_TrueNasSourceFails_PreservesLastKnownActiveState()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        var client = new FakeOperationsSystemClient
        {
            Alerts = [new TrueNasAlertDto { Uuid = "preserved-alert", Level = "WARNING", Text = "Keep this open.", CreatedAt = now }]
        };
        await using var provider = CreateProvider(database, new FakeWebPushSender(), now);
        var service = CreateService(database, client, provider, now);
        await service.RefreshAsync();

        client.AlertException = new TrueNasClientException("EACCES", "Not authorized");
        var refresh = await service.RefreshAsync();
        var item = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(candidate => candidate.Kind == OperationsInboxKind.TrueNasAlert);

        Assert.HasCount(1, refresh.Warnings);
        StringAssert.Contains(refresh.Warnings[0], "EACCES");
        Assert.AreEqual(OperationsInboxStatus.Open, item.Status);
        Assert.IsTrue(item.IsSourceActive);
    }

    [TestMethod]
    [TestCategory("Regression")]
    public async Task RefreshAsync_NotificationDeliveryFailure_DoesNotCreateRecursivePush()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        await using (var db = await database.CreateDbContextAsync())
        {
            db.Notifications.Add(new NotificationRecord { EventType = NotificationEventType.AutomaticUpdateFailed, Provider = NotificationProvider.Push, Status = DeliveryStatus.Failed, DeduplicationKey = "failed-push", CreatedUtc = now.UtcDateTime, ErrorSummary = "Endpoint unavailable." });
            await db.SaveChangesAsync();
        }

        var pushSender = new FakeWebPushSender(hasSubscriptions: true);
        await using var provider = CreateProvider(database, pushSender, now);
        var service = CreateService(database, new FakeOperationsSystemClient(), provider, now);

        await service.RefreshAsync();
        var item = (await service.GetSnapshotAsync(new OperationsInboxQuery())).Items.Single(candidate => candidate.Kind == OperationsInboxKind.NotificationFailure);

        Assert.AreEqual(0, pushSender.Calls);
        Assert.AreEqual(OperationsInboxPushState.NotRequested, item.PushState);
    }

    [TestMethod]
    public async Task GetSnapshotAsync_FiltersByStateSourceSeverityAndTime()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        var client = new FakeOperationsSystemClient
        {
            Alerts =
            [
                new TrueNasAlertDto { Uuid = "critical-alert", Level = "CRITICAL", Text = "Pool issue", CreatedAt = now },
                new TrueNasAlertDto { Uuid = "info-alert", Level = "INFO", Text = "Routine notice", CreatedAt = now.AddDays(-10) }
            ]
        };
        await using var provider = CreateProvider(database, new FakeWebPushSender(), now);
        var service = CreateService(database, client, provider, now);
        await service.RefreshAsync();

        var snapshot = await service.GetSnapshotAsync(new OperationsInboxQuery("Pool", OperationsInboxStatus.Open, OperationsInboxSource.TrueNas, OperationsInboxSeverity.Critical, now.AddDays(-1).UtcDateTime));

        Assert.HasCount(1, snapshot.Items);
        Assert.AreEqual("Pool issue", snapshot.Items[0].Summary);
    }

    [TestMethod]
    [TestCategory("Regression")]
    public async Task GetSnapshotAsync_ActiveOnly_ExcludesResolvedHistory()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
        var client = new FakeOperationsSystemClient
        {
            Alerts = [new TrueNasAlertDto { Uuid = "resolved-alert", Level = "WARNING", Text = "Recovered issue", CreatedAt = now }]
        };
        await using var provider = CreateProvider(database, new FakeWebPushSender(), now);
        var service = CreateService(database, client, provider, now);
        await service.RefreshAsync();
        client.Alerts = [];
        await service.RefreshAsync();
        client.Alerts = [new TrueNasAlertDto { Uuid = "active-alert", Level = "ERROR", Text = "Active issue", CreatedAt = now }];
        await service.RefreshAsync();

        var active = await service.GetSnapshotAsync(new OperationsInboxQuery(IncludeResolved: false));
        var all = await service.GetSnapshotAsync(new OperationsInboxQuery());

        Assert.HasCount(1, active.Items);
        Assert.AreEqual("Active issue", active.Items[0].Summary);
        Assert.IsFalse(active.Items.Any(item => item.Status == OperationsInboxStatus.Resolved));
        Assert.HasCount(2, all.Items);
        Assert.AreEqual(1, active.ResolvedCount);
    }

    private static async Task SeedLocalSourcesAsync(TestDatabase database, DateTime now)
    {
        await using var db = await database.CreateDbContextAsync();
        var app = new AppRecord { Id = "plex", Name = "Plex" };
        var run = new UpdateRun { Trigger = RunTrigger.CheckAndUpdateNow, StartedUtc = now.AddMinutes(-10), Status = RunStatus.Failed };
        db.Apps.Add(app);
        db.UpdateRuns.Add(run);
        db.UpdateAttempts.Add(new UpdateAttempt { Run = run, App = app, AppId = app.Id, Kind = AttemptKind.CatalogUpgrade, StartedUtc = now.AddMinutes(-10), EndedUtc = now.AddMinutes(-9), Status = AttemptStatus.Failed, ReasonCode = "JOB_FAILED", ReasonMessage = "Image pull failed." });
        db.UptimeKumaMonitors.Add(new UptimeKumaMonitorRecord { MonitorId = "kuma-1", App = app, AppId = app.Id, Name = "Plex External", Type = "http", Url = "https://plex.example.test", Status = UptimeKumaMonitorStatus.Down, IsPresent = true, LastSeenUtc = now.AddMinutes(-2) });
        db.Notifications.Add(new NotificationRecord { EventType = NotificationEventType.AutomaticUpdateFailed, Provider = NotificationProvider.Email, Status = DeliveryStatus.Failed, DeduplicationKey = "email-failure", CreatedUtc = now.AddMinutes(-3), ErrorSummary = "SMTP rejected the message." });
        await db.SaveChangesAsync();
    }

    private static ServiceProvider CreateProvider(TestDatabase database, IWebPushNotificationSender pushSender, DateTimeOffset now)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebPushNotificationSender>(pushSender);
        services.AddScoped<IEmailNotificationSender, FakeEmailSender>();
        services.AddScoped<IWebhookNotificationSender, FakeWebhookSender>();
        services.AddScoped<INotificationDispatcher>(provider => new NotificationDispatcher(database, provider.GetRequiredService<IEmailNotificationSender>(), provider.GetRequiredService<IWebhookNotificationSender>(), provider.GetRequiredService<IWebPushNotificationSender>(), new FixedTimeProvider(now)));
        return services.BuildServiceProvider();
    }

    private static OperationsInboxService CreateService(TestDatabase database, ITrueNasSystemClient client, IServiceProvider services, DateTimeOffset now) => new(database, client, services.GetRequiredService<IServiceScopeFactory>(), new FixedTimeProvider(now), NullLogger<OperationsInboxService>.Instance);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeOperationsSystemClient : ITrueNasSystemClient
    {
        public IReadOnlyList<TrueNasAlertDto> Alerts { get; set; } = [];
        public Exception? AlertException { get; set; }
        public IReadOnlyList<TrueNasJobDto> Jobs { get; set; } = [];
        public IReadOnlyList<TrueNasPoolDto> Pools { get; set; } = [];

        public Task<TrueNasSystemInfoDto> GetSystemInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TrueNasSystemInfoDto());

        public Task<IReadOnlyList<TrueNasAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default) => AlertException is null ? Task.FromResult(Alerts) : Task.FromException<IReadOnlyList<TrueNasAlertDto>>(AlertException);

        public Task<TrueNasUpdateStatusDto> GetUpdateStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TrueNasUpdateStatusDto());

        public Task<IReadOnlyList<TrueNasPoolDto>> QueryPoolsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Pools);

        public Task<IReadOnlyList<TrueNasJobDto>> ListJobsAsync(int limit = 200, CancellationToken cancellationToken = default) => Task.FromResult(Jobs);

        public async IAsyncEnumerable<IReadOnlyList<TrueNasAppStatsDto>> WatchAppStatsAsync(int intervalSeconds = 5, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
