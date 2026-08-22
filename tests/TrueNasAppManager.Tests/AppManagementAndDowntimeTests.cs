using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class AppManagementAndDowntimeTests
{
    /// <summary>Verifies that health monitoring sends one alert per incident plus a recovery event.</summary>
    [TestMethod]
    public async Task Discovery_DowntimeMonitoring_NotifiesOncePerIncident()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedAppAsync(database, notifyOnDowntime: true);
        var trueNas = new LifecycleTrueNasClient { State = "STOPPED" };
        var notifications = new NoopNotificationDispatcher();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero));
        var discovery = new AppDiscoveryService(trueNas, database, time);
        var management = new AppManagementService(trueNas, database, time, NullLogger<AppManagementService>.Instance);
        var health = new AppHealthMonitorService(database, management, notifications, time);

        await discovery.DiscoverAsync();
        await health.EvaluateAsync(["immich"]);
        await discovery.DiscoverAsync();
        await health.EvaluateAsync(["immich"]);
        trueNas.State = "RUNNING";
        await discovery.DiscoverAsync();
        await health.EvaluateAsync(["immich"]);
        trueNas.State = "CRASHED";
        await discovery.DiscoverAsync();
        await health.EvaluateAsync(["immich"]);

        Assert.HasCount(3, notifications.Events);
        CollectionAssert.AreEqual(new[] { NotificationEventType.AppDowntime, NotificationEventType.AppRecoverySucceeded, NotificationEventType.AppDowntime }, notifications.Events.Select(item => item.EventType).ToArray());
        Assert.AreNotEqual(notifications.Events[0].DeduplicationKey, notifications.Events[2].DeduplicationKey);
        await using var db = await database.CreateDbContextAsync();
        Assert.IsTrue((await db.Apps.SingleAsync()).DowntimeNotificationActive);
    }

    /// <summary>Verifies that lifecycle actions wait for TrueNAS and persist the resulting state.</summary>
    /// <param name="action">The lifecycle action under test.</param>
    /// <param name="expectedState">The expected persisted TrueNAS state.</param>
    [TestMethod]
    [DataRow(AppLifecycleAction.Start, "RUNNING")]
    [DataRow(AppLifecycleAction.Stop, "STOPPED")]
    [DataRow(AppLifecycleAction.Restart, "RUNNING")]
    public async Task AppManagement_ExecutesJobAndPersistsState(AppLifecycleAction action, string expectedState)
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedAppAsync(database, notifyOnDowntime: true, downtimeNotificationActive: true);
        var trueNas = new LifecycleTrueNasClient();
        var service = new AppManagementService(trueNas, database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero)), NullLogger<AppManagementService>.Instance);

        var result = await service.ExecuteAsync("immich", action);

        Assert.IsTrue(result.Success);
        var expectedCalls = action == AppLifecycleAction.Restart
            ? new[] { AppLifecycleAction.Stop, AppLifecycleAction.Start }
            : new[] { action };
        CollectionAssert.AreEqual(expectedCalls, trueNas.LifecycleCalls);
        Assert.AreEqual(expectedCalls.Length, trueNas.WaitCalls);
        await using var db = await database.CreateDbContextAsync();
        var app = await db.Apps.SingleAsync();
        Assert.AreEqual(expectedState, app.State);
        Assert.IsFalse(app.DowntimeNotificationActive);
    }

    /// <summary>Verifies that a stop requested through the manager does not produce a downtime alert on the next check.</summary>
    [TestMethod]
    public async Task AppManagement_IntentionalStop_SuppressesDowntimeAlert()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedAppAsync(database, notifyOnDowntime: true);
        var trueNas = new LifecycleTrueNasClient();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero));
        var service = new AppManagementService(trueNas, database, time, NullLogger<AppManagementService>.Instance);
        var notifications = new NoopNotificationDispatcher();
        var discovery = new AppDiscoveryService(trueNas, database, time);

        await service.ExecuteAsync("immich", AppLifecycleAction.Stop);
        await discovery.DiscoverAsync();

        Assert.IsEmpty(notifications.Events);
    }

    private static async Task SeedAppAsync(TestDatabase database, bool notifyOnDowntime, bool downtimeNotificationActive = false)
    {
        await using var db = await database.CreateDbContextAsync();
        db.Apps.Add(new AppRecord
        {
            Id = "immich",
            Name = "Immich",
            State = "RUNNING",
            Policy = AppPolicy.AutoUpdate,
            NotifyOnDowntime = notifyOnDowntime,
            DowntimeAction = notifyOnDowntime ? DowntimeAction.NotifyOnly : DowntimeAction.Ignore,
            DowntimeNotificationActive = downtimeNotificationActive,
            LastSeenUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed class LifecycleTrueNasClient : ITrueNasClient
    {
        private AppLifecycleAction? pendingAction;

        public bool? HasWriteAccess => true;
        public bool? HasMailWriteAccess => true;
        public string State { get; set; } = "RUNNING";
        public List<AppLifecycleAction> LifecycleCalls { get; } = [];
        public int WaitCalls { get; private set; }

        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "Connected", true, true));

        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrueNasAppDto>>([App()]);

        public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(App());

        public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(string appId, string targetVersion = "latest", CancellationToken cancellationToken = default) => Task.FromResult(new TrueNasUpgradeSummaryDto());

        public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default) => BeginAsync(AppLifecycleAction.Start);

        public Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default) => BeginAsync(AppLifecycleAction.Stop);

        public Task<long> StartUpgradeAsync(string appId, string targetVersion, bool snapshotHostPaths, CancellationToken cancellationToken = default) => Task.FromResult(1L);

        public Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(1L);

        public Task<long> StartRollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default) => Task.FromResult(1L);

        public Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default)
        {
            WaitCalls++;
            State = pendingAction == AppLifecycleAction.Stop ? "STOPPED" : "RUNNING";
            return Task.CompletedTask;
        }

        public Task ResetConnectionAsync() => Task.CompletedTask;

        public Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private Task<long> BeginAsync(AppLifecycleAction action)
        {
            pendingAction = action;
            LifecycleCalls.Add(action);
            return Task.FromResult(42L);
        }

        private TrueNasAppDto App() => new()
        {
            Id = "immich",
            Name = "Immich",
            State = State,
            Version = "1.0.0",
            HumanVersion = "1.0.0"
        };
    }
}
