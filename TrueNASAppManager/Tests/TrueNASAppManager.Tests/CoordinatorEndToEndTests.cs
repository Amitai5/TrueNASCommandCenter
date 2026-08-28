using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class CoordinatorEndToEndTests
{
    [TestMethod]
    public async Task CheckAndUpdate_DiscoversSequentiallyUpdatesVerifiesAndPersists()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedPoliciesAsync(database);
        var trueNas = new FakeTrueNasClient();
        var coordinator = CreateCoordinator(database, trueNas);

        var result = await coordinator.RunAsync(RunTrigger.CheckAndUpdateNow, executeUpdates: true);

        Assert.AreEqual(RunStatus.Succeeded, result.Status);
        Assert.AreEqual(2, result.Checked);
        Assert.AreEqual(2, result.Succeeded);
        CollectionAssert.AreEqual(new[] { "catalog", "image" }, trueNas.StartOrder);
        Assert.AreEqual(1, trueNas.MaximumConcurrentJobs);

        await using var db = await database.CreateDbContextAsync();
        var attempts = await db.UpdateAttempts.OrderBy(attempt => attempt.StartedUtc).ToListAsync();
        Assert.HasCount(2, attempts);
        Assert.IsTrue(attempts.All(attempt => attempt.Status == AttemptStatus.Succeeded));
        Assert.IsTrue((await db.Apps.ToListAsync()).All(app => app.LastSuccessfulUpdateUtc is not null));
        var run = await db.UpdateRuns.SingleAsync();
        Assert.AreEqual(2, run.SucceededCount);
        Assert.AreEqual(RunStatus.Succeeded, run.Status);
    }

    [TestMethod]
    public async Task CheckAndUpdate_RefreshesCompleteInventoryBeforeStartingAnyUpdate()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedPoliciesAsync(database);
        var trueNas = new FakeTrueNasClient();
        var coordinator = CreateCoordinator(database, trueNas);

        await coordinator.CheckAndUpdateAsync(RunTrigger.CheckAndUpdateNow, true);

        Assert.IsNotEmpty(trueNas.CallOrder);
        Assert.AreEqual("refresh", trueNas.CallOrder[0]);
        Assert.IsTrue(trueNas.CallOrder.Skip(1).Any(call => call.StartsWith("start:", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckAndUpdate_CheckOnlyRefreshesInventoryWithoutStartingUpdates()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedPoliciesAsync(database);
        var trueNas = new FakeTrueNasClient();
        var coordinator = CreateCoordinator(database, trueNas);

        var result = await coordinator.CheckAndUpdateAsync(RunTrigger.CheckNow, executeUpdates: false);

        Assert.AreEqual(RunStatus.Succeeded, result.Status);
        Assert.IsNotEmpty(trueNas.CallOrder);
        Assert.AreEqual("refresh", trueNas.CallOrder[0]);
        Assert.IsEmpty(trueNas.StartOrder);
    }

    [TestMethod]
    public async Task AppFailure_DoesNotStopLaterEligibleApps()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedPoliciesAsync(database);
        var trueNas = new FakeTrueNasClient { FailCatalogJob = true };
        var coordinator = CreateCoordinator(database, trueNas);

        var result = await coordinator.RunAsync(RunTrigger.CheckAndUpdateNow, executeUpdates: true);

        Assert.AreEqual(RunStatus.PartiallySucceeded, result.Status);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, result.Succeeded);
        CollectionAssert.AreEqual(new[] { "catalog", "image" }, trueNas.StartOrder);
    }

    [TestMethod]
    public async Task MissingWriteRole_BlocksAutomaticExecution()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedPoliciesAsync(database);
        var trueNas = new FakeTrueNasClient { WriteAccess = false };
        var coordinator = CreateCoordinator(database, trueNas);

        var result = await coordinator.RunAsync(RunTrigger.CheckAndUpdateNow, executeUpdates: true);

        Assert.AreEqual(RunStatus.Succeeded, result.Status);
        Assert.IsEmpty(trueNas.StartOrder);
        await using var db = await database.CreateDbContextAsync();
        Assert.IsTrue((await db.UpdateAttempts.ToListAsync()).All(
            attempt => attempt.Status == AttemptStatus.Blocked &&
                       attempt.ReasonCode == "MISSING_WRITE_ACCESS"));
    }

    [TestMethod]
    public async Task ServerWideFailure_StopsLaterQueuedUpdates()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedPoliciesAsync(database);
        var trueNas = new FakeTrueNasClient { FailCatalogForServer = true };
        var coordinator = CreateCoordinator(database, trueNas);

        var result = await coordinator.RunAsync(RunTrigger.CheckAndUpdateNow, executeUpdates: true);

        Assert.AreEqual(RunStatus.Failed, result.Status);
        CollectionAssert.AreEqual(new[] { "catalog" }, trueNas.StartOrder);
        await using var db = await database.CreateDbContextAsync();
        Assert.IsTrue(await db.UpdateAttempts.AnyAsync(
            attempt => attempt.Status == AttemptStatus.Skipped &&
                       attempt.ReasonCode == "SERVER_CONDITION"));
    }

    [TestMethod]
    [DataRow(5, "busy")]
    [DataRow(6, "busy")]
    [DataRow(8, "not writable")]
    [DataRow(13, "full")]
    [DataRow(19, "conflicted")]
    public async Task DatabaseFailure_ReturnsActionableMessageAndLogsOriginalException(int sqliteErrorCode, string expectedMessage)
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var trueNas = new FakeTrueNasClient();
        var logger = new RecordingLogger<UpdateCoordinator>();
        var failure = new DbUpdateException("Database write failed.", new SqliteException("SQLite test failure.", sqliteErrorCode));
        var coordinator = CreateCoordinator(database, trueNas, new FailingDiscoveryService(failure), logger);

        var result = await coordinator.RefreshAppsAsync();

        Assert.AreEqual(RunStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, expectedMessage, StringComparison.OrdinalIgnoreCase);
        var loggedFailure = logger.Entries.Single(entry => entry.Level == LogLevel.Warning);
        Assert.AreSame(failure, loggedFailure.Exception);
    }

    private static UpdateCoordinator CreateCoordinator(TestDatabase database, FakeTrueNasClient trueNas, IAppDiscoveryService? discoveryOverride = null, ILogger<UpdateCoordinator>? logger = null)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero));
        var settings = database.CreateSettingsService();
        var discovery = discoveryOverride ?? new AppDiscoveryService(trueNas, database, time);
        var executor = new UpdateExecutor(
            trueNas,
            discovery,
            database,
            settings,
            time,
            NullLogger<UpdateExecutor>.Instance);
        return new UpdateCoordinator(
            new RunLock(),
            discovery,
            new NoopAppHealthMonitorService(),
            new NoopGitHubMetadataService(),
            trueNas,
            new UpdatePolicyEvaluator(new VersionClassifier()),
            executor,
            new NoopNotificationDispatcher(),
            database,
            settings,
            time,
            logger ?? NullLogger<UpdateCoordinator>.Instance);
    }

    private static async Task SeedPoliciesAsync(TestDatabase database)
    {
        await using var db = await database.CreateDbContextAsync();
        db.Apps.AddRange(
            new AppRecord
            {
                Id = "catalog",
                Name = "Catalog",
                State = "RUNNING",
                Policy = AppPolicy.AutoUpdate,
                VersionScope = VersionScope.AnyVersion,
                LastSeenUtc = DateTime.UtcNow
            },
            new AppRecord
            {
                Id = "image",
                Name = "Image",
                State = "RUNNING",
                Policy = AppPolicy.AutoUpdate,
                VersionScope = VersionScope.PatchOnly,
                LastSeenUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
    }

    private sealed class FakeTrueNasClient : ITrueNasClient
    {
        private readonly Dictionary<long, string> jobs = [];
        private readonly Dictionary<string, TrueNasAppDto> apps = new()
        {
            ["catalog"] = App("catalog", catalog: true, image: false, version: "1.0.0", latest: "1.1.0"),
            ["image"] = App("image", catalog: false, image: true, version: "3.7.4", latest: null)
        };
        private int activeJobs;
        private long nextJob;

        public bool FailCatalogJob { get; init; }
        public bool FailCatalogForServer { get; init; }
        public bool WriteAccess { get; init; } = true;
        public bool? HasWriteAccess => WriteAccess;
        public List<string> StartOrder { get; } = [];
        public List<string> CallOrder { get; } = [];
        public int MaximumConcurrentJobs { get; private set; }

        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionTestResult(true, "Connected", true, true));

        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default)
        {
            CallOrder.Add("refresh");
            return Task.FromResult<IReadOnlyList<TrueNasAppDto>>(apps.Values.OrderBy(app => app.Id).ToList());
        }

        public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) =>
            Task.FromResult(apps[appId]);

        public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(
            string appId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                apps[appId].ImageUpdatesAvailable ? ["example/image:latest"] : []);

        public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(
            string appId,
            string targetVersion = "latest",
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrueNasUpgradeSummaryDto
            {
                LatestVersion = "1.1.0",
                LatestHumanVersion = "1.1.0",
                UpgradeVersion = "1.1.0",
                UpgradeHumanVersion = "1.1.0",
                AvailableVersions = [new TrueNasVersionInfoDto { Version = "1.1.0", HumanVersion = "1.1.0" }]
            });

        public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(
            string appId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default) => StartAsync(appId);

        public Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default) => StartAsync(appId);

        public Task<long> StartUpgradeAsync(
            string appId,
            string targetVersion,
            bool snapshotHostPaths,
            CancellationToken cancellationToken = default)
        {
            if (FailCatalogForServer)
            {
                StartOrder.Add(appId);
                throw new TrueNasClientException("NETWORK_ERROR", "Connection lost.");
            }

            return StartAsync(appId);
        }

        public Task<long> StartImageRefreshAsync(
            string appId,
            CancellationToken cancellationToken = default) =>
            StartAsync(appId);

        public Task<long> StartRollbackAsync(
            string appId,
            string targetVersion,
            CancellationToken cancellationToken = default) =>
            StartAsync(appId);

        public Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default)
        {
            var appId = jobs[jobId];
            activeJobs++;
            MaximumConcurrentJobs = Math.Max(MaximumConcurrentJobs, activeJobs);
            try
            {
                if (appId == "catalog" && FailCatalogJob)
                {
                    throw new TrueNasClientException("JOB_FAILED", "The catalog job failed.");
                }

                var current = apps[appId];
                apps[appId] = current with
                {
                    Version = appId == "catalog" ? "1.1.0" : current.Version,
                    HumanVersion = appId == "catalog" ? "1.1.0" : current.HumanVersion,
                    UpgradeAvailable = false,
                    ImageUpdatesAvailable = false,
                    LatestVersion = appId == "catalog" ? "1.1.0" : current.LatestVersion
                };
                return Task.CompletedTask;
            }
            finally
            {
                activeJobs--;
            }
        }

        public Task ResetConnectionAsync() => Task.CompletedTask;

        public Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private Task<long> StartAsync(string appId)
        {
            StartOrder.Add(appId);
            CallOrder.Add($"start:{appId}");
            var jobId = ++nextJob;
            jobs[jobId] = appId;
            return Task.FromResult(jobId);
        }

        private static TrueNasAppDto App(
            string id,
            bool catalog,
            bool image,
            string version,
            string? latest) =>
            new()
            {
                Id = id,
                Name = id,
                State = "RUNNING",
                UpgradeAvailable = catalog,
                ImageUpdatesAvailable = image,
                CustomApp = false,
                HumanVersion = version,
                Version = version,
                LatestVersion = latest,
                ActionRequired = false
            };
    }

    private sealed class FailingDiscoveryService(Exception exception) : IAppDiscoveryService
    {
        public Task<InventoryRefreshResult> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromException<InventoryRefreshResult>(exception);

        public Task<IReadOnlyList<AppRecord>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<AppRecord>>(exception);

        public Task<AppRecord> DiscoverAppAsync(string appId, CancellationToken cancellationToken = default) => Task.FromException<AppRecord>(exception);
    }
}
