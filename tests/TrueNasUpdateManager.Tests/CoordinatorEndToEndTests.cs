using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Integrations.TrueNas;
using TrueNasUpdateManager.Services;

namespace TrueNasUpdateManager.Tests;

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

    private static UpdateCoordinator CreateCoordinator(TestDatabase database, FakeTrueNasClient trueNas)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero));
        var settings = database.CreateSettingsService();
        var discovery = new AppDiscoveryService(trueNas, database, time);
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
            new UpdatePolicyEvaluator(new VersionClassifier()),
            executor,
            new NoopNotificationDispatcher(),
            database,
            settings,
            time,
            NullLogger<UpdateCoordinator>.Instance);
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
        public List<string> StartOrder { get; } = [];
        public int MaximumConcurrentJobs { get; private set; }

        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionTestResult(true, "Connected", true, true));

        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrueNasAppDto>>(apps.Values.OrderBy(app => app.Id).ToList());

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

        public Task<long> StartUpgradeAsync(
            string appId,
            string targetVersion,
            bool snapshotHostPaths,
            CancellationToken cancellationToken = default) =>
            StartAsync(appId);

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

        private Task<long> StartAsync(string appId)
        {
            StartOrder.Add(appId);
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
}
