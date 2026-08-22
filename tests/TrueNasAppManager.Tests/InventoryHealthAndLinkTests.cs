using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class InventoryHealthAndLinkTests
{
    [TestMethod]
    public async Task Refresh_MapsWorkloadsAndPreservesMissingAppHistory()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await using (var seed = await database.CreateDbContextAsync())
        {
            seed.Apps.Add(new AppRecord { Id = "removed", Name = "Removed", IsInstalled = true, LastSeenUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var client = new InventoryTrueNasClient([AppWithWorkloads()]);
        var now = new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);
        var service = new AppDiscoveryService(client, database, new FixedTimeProvider(now));

        var result = await service.RefreshAsync();

        Assert.AreEqual(1, result.Discovered);
        Assert.AreEqual(1, result.Missing);
        await using var db = await database.CreateDbContextAsync();
        var app = await db.Apps.Include(item => item.Ports).Include(item => item.Portals).Include(item => item.Containers).SingleAsync(item => item.Id == "immich");
        Assert.AreEqual("Photo management", app.Description);
        Assert.AreEqual("community", app.Train);
        Assert.AreEqual(AppHealthState.Running, app.HealthState);
        Assert.HasCount(1, app.Ports);
        Assert.AreEqual(2283, app.Ports.Single().HostPort);
        Assert.AreEqual(2283, app.Ports.Single().ContainerPort);
        Assert.AreEqual("server", app.Ports.Single().ContainerName);
        Assert.HasCount(1, app.Portals);
        Assert.HasCount(1, app.Containers);
        StringAssert.Contains(app.Containers.Single().NetworksJson!, "ix-immich");
        StringAssert.Contains(app.Containers.Single().VolumesJson!, "library");
        Assert.IsFalse((await db.Apps.SingleAsync(item => item.Id == "removed")).IsInstalled);
        Assert.AreEqual(now.UtcDateTime, (await db.Settings.SingleAsync()).LastInventoryRefreshUtc);
    }

    [TestMethod]
    public async Task HealthRecovery_AttemptsRestartOnlyOncePerIncident()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await using (var seed = await database.CreateDbContextAsync())
        {
            seed.Apps.Add(new AppRecord
            {
                Id = "plex",
                Name = "Plex",
                IsInstalled = true,
                State = "STOPPED",
                HealthState = AppHealthState.Stopped,
                DowntimeAction = DowntimeAction.RestartAndNotify,
                LastSeenUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var management = new FailingRecoveryService();
        var notifications = new NoopNotificationDispatcher();
        var service = new AppHealthMonitorService(database, management, notifications, new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero)));

        await service.EvaluateAsync(["plex"]);
        await service.EvaluateAsync(["plex"]);

        Assert.AreEqual(1, management.Calls);
        CollectionAssert.AreEqual(new[] { NotificationEventType.AppDowntime, NotificationEventType.AppRecoveryFailed }, notifications.Events.Select(item => item.EventType).ToArray());
        await using var db = await database.CreateDbContextAsync();
        var app = await db.Apps.SingleAsync();
        Assert.IsNotNull(app.HealthIncidentId);
        Assert.IsNotNull(app.RecoveryAttemptedUtc);
    }

    [TestMethod]
    public void LinkResolver_UsesManualUrlBeforePortal()
    {
        var app = new AppRecord
        {
            ManualPortalUrl = "https://photos.example.test/",
            Portals = [new AppPortalRecord { Url = "http://0.0.0.0:2283/" }]
        };

        var result = new AppLinkService().ResolveWebUiUrl(app, "https://nas.example.test");

        Assert.AreEqual("https://photos.example.test/", result);
    }

    [TestMethod]
    public void LinkResolver_RewritesTrueNasPortalWithApprovedHost()
    {
        var app = new AppRecord { Portals = [new AppPortalRecord { Url = "http://0.0.0.0:2283/" }] };

        var result = new AppLinkService().ResolveWebUiUrl(app, "https://nas.example.test");

        Assert.AreEqual("https://nas.example.test:2283/", result);
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///etc/passwd")]
    [DataRow("not a url")]
    public void LinkResolver_RejectsUnsafeManualUrls(string unsafeUrl)
    {
        var app = new AppRecord { ManualPortalUrl = unsafeUrl };

        var result = new AppLinkService().ResolveWebUiUrl(app, null);

        Assert.IsNull(result);
    }

    private static TrueNasAppDto AppWithWorkloads() => new()
    {
        Id = "immich",
        Name = "Immich",
        State = "RUNNING",
        Version = "1.14.34",
        HumanVersion = "v1.14.34",
        Metadata = Json("""{"description":"Photo management","home":"https://immich.app","icon":"https://example.test/icon.png","train":"community","sources":["https://github.com/immich-app/immich"]}"""),
        ActiveWorkloads = Json("""{"used_ports":[{"container_port":2283,"protocol":"tcp","host_ports":[{"host_ip":"0.0.0.0","host_port":2283}]}],"container_details":[{"id":"abc","service_name":"server","image":"ghcr.io/immich-app/immich-server:release","state":"running","port_config":[{"container_port":2283,"protocol":"tcp","host_ports":[{"host_ip":"0.0.0.0","host_port":2283}]}],"volume_mounts":[{"source":"library","destination":"/data","mode":"rw","type":"volume"}]}],"networks":[{"Name":"ix-immich","Id":"network-1","Labels":{}}],"volumes":[{"source":"library","destination":"/data","mode":"rw","type":"volume"}]}"""),
        Portals = Json("""{"web_ui":"http://0.0.0.0:2283/"}""")
    };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FailingRecoveryService : IAppManagementService
    {
        public int Calls { get; private set; }

        public Task<AppManagementResult> ExecuteAsync(string appId, AppLifecycleAction action, CancellationToken cancellationToken = default) => Task.FromResult(new AppManagementResult(true, "Managed"));

        public Task<AppManagementResult> ExecuteAutomaticRecoveryAsync(string appId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AppManagementResult(false, "Restart failed", ErrorCode: "JOB_FAILED"));
        }
    }

    private sealed class InventoryTrueNasClient(IReadOnlyList<TrueNasAppDto> apps) : ITrueNasClient
    {
        public bool? HasWriteAccess => true;
        public bool? HasMailWriteAccess => true;
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "Connected", true, true, true));
        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) => Task.FromResult(apps);
        public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(apps.Single(app => app.Id == appId));
        public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(string appId, string targetVersion = "latest", CancellationToken cancellationToken = default) => Task.FromResult(new TrueNasUpgradeSummaryDto());
        public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<long> StartUpgradeAsync(string appId, string targetVersion, bool snapshotHostPaths, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<long> StartRollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task ResetConnectionAsync() => Task.CompletedTask;
    }
}
