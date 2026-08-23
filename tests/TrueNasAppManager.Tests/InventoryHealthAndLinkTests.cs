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
    public async Task Refresh_ReplacesExistingWorkloadsWithoutConcurrencyFailure()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = new AppDiscoveryService(new InventoryTrueNasClient([AppWithWorkloads()]), database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero)));

        await service.RefreshAsync();
        await service.RefreshAsync();

        await using var db = await database.CreateDbContextAsync();
        var app = await db.Apps.Include(item => item.Ports).Include(item => item.Portals).Include(item => item.Containers).SingleAsync();
        Assert.HasCount(1, app.Ports);
        Assert.HasCount(1, app.Portals);
        Assert.HasCount(1, app.Containers);
    }

    [TestMethod]
    public async Task Refresh_ReplacesStaleDegradedHealthWithCurrentRunningState()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await using (var seed = await database.CreateDbContextAsync())
        {
            seed.Apps.Add(new AppRecord { Id = "immich", Name = "Immich", State = "RUNNING", HealthState = AppHealthState.Degraded, LastSeenUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }
        var service = new AppDiscoveryService(new InventoryTrueNasClient([AppWithWorkloads()]), database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero)));

        await service.RefreshAsync();

        await using var db = await database.CreateDbContextAsync();
        Assert.AreEqual(AppHealthState.Running, (await db.Apps.SingleAsync()).HealthState);
    }

    [TestMethod]
    [DataRow("exited", AppHealthState.Running)]
    [DataRow("starting", AppHealthState.Running)]
    [DataRow("crashed", AppHealthState.Degraded)]
    [DataRow("failed", AppHealthState.Degraded)]
    [DataRow("error", AppHealthState.Degraded)]
    public async Task Refresh_ClassifiesContainerStateWithoutFlaggingCompletedOneShotContainers(string containerState, AppHealthState expectedHealth)
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var app = AppWithWorkloads() with
        {
            ActiveWorkloads = Json($$"""{"container_details":[{"id":"container-1","service_name":"permissions","image":"example.test/permissions:latest","state":"{{containerState}}","port_config":[],"volume_mounts":[]}]}""")
        };
        var service = new AppDiscoveryService(new InventoryTrueNasClient([app]), database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero)));

        await service.RefreshAsync();

        await using var db = await database.CreateDbContextAsync();
        Assert.AreEqual(expectedHealth, (await db.Apps.SingleAsync()).HealthState);
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
    public void LinkResolver_SelectsConfiguredUrlForCurrentManagerRoute()
    {
        var app = new AppRecord
        {
            LocalPortalUrl = "http://truenas.local:2283/",
            RemotePortalUrl = "https://photos.example.test/",
            Portals = [new AppPortalRecord { Url = "http://0.0.0.0:2283/" }]
        };
        var service = new AppLinkService();

        var local = service.ResolveWebUiLinks(app, "https://nas.example.test", new Uri("http://truenas.local:2600/apps/immich"));
        var remote = service.ResolveWebUiLinks(app, "https://nas.example.test", new Uri("https://apps.amitai.tech/apps/immich"));

        Assert.AreEqual(WebUiRoute.Local, local.SelectedRoute);
        Assert.AreEqual("http://truenas.local:2283/", local.SelectedUrl);
        Assert.AreEqual(WebUiRoute.Remote, remote.SelectedRoute);
        Assert.AreEqual("https://photos.example.test/", remote.SelectedUrl);
    }

    [TestMethod]
    public void LinkResolver_RewritesTrueNasPortalWithApprovedHost()
    {
        var app = new AppRecord { Portals = [new AppPortalRecord { Url = "http://0.0.0.0:2283/" }] };

        var result = new AppLinkService().ResolveWebUiLinks(app, "https://nas.example.test", new Uri("http://truenas.local:2600"));

        Assert.AreEqual("https://nas.example.test:2283/", result.LocalUrl);
        Assert.AreEqual(result.LocalUrl, result.SelectedUrl);
    }

    [TestMethod]
    public void LinkResolver_DoesNotGuessRemoteUrlFromTrueNasPortal()
    {
        var app = new AppRecord { Portals = [new AppPortalRecord { Url = "http://0.0.0.0:2283/" }] };

        var result = new AppLinkService().ResolveWebUiLinks(app, "https://nas.example.test", new Uri("https://apps.amitai.tech"));

        Assert.AreEqual(WebUiRoute.Remote, result.SelectedRoute);
        Assert.IsNull(result.RemoteUrl);
        Assert.IsNull(result.SelectedUrl);
    }

    [TestMethod]
    public void LinkResolver_BuildsLocalUrlFromPrivateManagerHostAndPublishedPort()
    {
        var app = new AppRecord { Ports = [new AppPortRecord { HostPort = 10704, Protocol = "tcp" }] };

        var result = new AppLinkService().ResolveWebUiLinks(app, null, new Uri("http://10.0.0.21:2600/apps/plex"));

        Assert.AreEqual(WebUiRoute.Local, result.SelectedRoute);
        Assert.AreEqual("http://10.0.0.21:10704/", result.LocalUrl);
        Assert.AreEqual(result.LocalUrl, result.SelectedUrl);
    }

    [TestMethod]
    [DataRow("http://10.0.0.21:10704/web", WebUiRoute.Local)]
    [DataRow("http://truenas.local:10704/web", WebUiRoute.Local)]
    [DataRow("https://plex.amitai.tech/", WebUiRoute.Remote)]
    public void LinkResolver_ClassifiesLegacyManualUrl(string legacyUrl, WebUiRoute expectedRoute)
    {
        var app = new AppRecord { ManualPortalUrl = legacyUrl };
        var managerUri = expectedRoute == WebUiRoute.Local ? new Uri("http://truenas.local:2600") : new Uri("https://apps.amitai.tech");

        var result = new AppLinkService().ResolveWebUiLinks(app, null, managerUri);

        Assert.AreEqual(expectedRoute, result.SelectedRoute);
        Assert.AreEqual(new Uri(legacyUrl).AbsoluteUri, result.SelectedUrl);
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///etc/passwd")]
    [DataRow("not a url")]
    [DataRow("https://user:password@example.test")]
    public void LinkResolver_RejectsUnsafeManualUrls(string unsafeUrl)
    {
        var app = new AppRecord { LocalPortalUrl = unsafeUrl, RemotePortalUrl = unsafeUrl };

        var result = new AppLinkService().ResolveWebUiLinks(app, null, new Uri("https://apps.amitai.tech"));

        Assert.IsNull(result.LocalUrl);
        Assert.IsNull(result.RemoteUrl);
        Assert.IsNull(result.SelectedUrl);
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
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "Connected", true, true));
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
