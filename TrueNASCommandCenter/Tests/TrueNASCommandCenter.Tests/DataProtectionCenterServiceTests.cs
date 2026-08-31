using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Scheduling;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class DataProtectionCenterServiceTests
{
    /// <summary>Verifies dataset coverage, snapshot age inputs, task state, and next-run calculation.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_VisibleProtectionData_MapsCoverageTasksAndWarnings()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => settings.TimeZoneId = "Etc/UTC");
        var client = new FakeDataProtectionClient
        {
            Datasets =
            [
                new TrueNasDatasetDto { Name = "tank" },
                new TrueNasDatasetDto { Name = "tank/media" },
                new TrueNasDatasetDto { Name = "tank/unprotected" },
                new TrueNasDatasetDto { Name = "tank/.system" }
            ],
            Snapshots =
            [
                new TrueNasSnapshotDto { Name = "tank/media@auto", Dataset = "tank/media", Properties = Json("{\"creation\":{\"rawvalue\":1788004800}}") }
            ],
            SnapshotTasks =
            [
                new TrueNasSnapshotTaskDto
                {
                    Id = 7,
                    Dataset = "tank/media",
                    Recursive = true,
                    Enabled = true,
                    Schedule = new TrueNasCronScheduleDto { Minute = "0", Hour = "0" },
                    State = Json("{\"state\":\"SUCCESS\",\"datetime\":\"2026-08-29T00:00:00Z\"}")
                }
            ],
            ReplicationTasks =
            [
                new TrueNasReplicationTaskDto { Id = 3, Name = "Media replica", Direction = "PUSH", SourceDatasets = ["tank/media"], TargetDataset = "backup/media", Enabled = true, State = Json("{\"state\":\"SUCCESS\"}") }
            ]
        };
        var service = CreateService(client, database, now);

        var result = await service.GetAsync();

        Assert.AreEqual(1, result.ProtectedDatasetCount);
        Assert.AreEqual(2, result.EligibleDatasetCount);
        Assert.AreEqual(1, result.UnprotectedDatasetCount);
        Assert.HasCount(2, result.Tasks);
        var media = result.Datasets.Single(dataset => dataset.Name == "tank/media");
        Assert.IsTrue(media.HasSnapshotTask);
        Assert.IsTrue(media.HasReplicationTask);
        Assert.AreEqual(1, media.SnapshotCount);
        Assert.IsNotNull(media.NewestSnapshotUtc);
        Assert.IsTrue(result.Datasets.Single(dataset => dataset.Name == "tank/.system").IsSystemDataset);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), result.Tasks.Single(task => task.Kind == DataProtectionTaskKind.Snapshot).NextRunUtc);
        Assert.IsTrue(result.Warnings.Any(warning => warning.Dataset == "tank/unprotected"));
    }

    /// <summary>Verifies one missing optional role does not hide protection data returned by other sources.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_MissingSnapshotRole_PreservesDatasetAndTaskSources()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var client = new FakeDataProtectionClient
        {
            Datasets = [new TrueNasDatasetDto { Name = "tank", Type = "FILESYSTEM" }, new TrueNasDatasetDto { Name = "tank/media", Type = "FILESYSTEM" }],
            SnapshotException = new TrueNasClientException("EACCES", "Not authorized"),
            SnapshotTasks = [new TrueNasSnapshotTaskDto { Id = 1, Dataset = "tank/media", Enabled = true }]
        };
        var service = CreateService(client, database, now);

        var result = await service.GetAsync();

        Assert.HasCount(2, result.Datasets);
        Assert.HasCount(1, result.Tasks);
        var snapshotSource = result.Sources.Single(source => source.RequiredRole == "SNAPSHOT_READ");
        Assert.IsFalse(snapshotSource.IsAvailable);
        StringAssert.Contains(snapshotSource.Error!, "SNAPSHOT_READ");
        Assert.IsTrue(result.Sources.Single(source => source.RequiredRole == "DATASET_READ").IsAvailable);
    }

    /// <summary>Verifies a failed enabled protection task becomes an actionable warning.</summary>
    [TestMethod]
    [TestCategory("Regression")]
    public async Task GetAsync_FailedCloudSyncTask_ReturnsFailureWarning()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var client = new FakeDataProtectionClient
        {
            CloudSyncTasks =
            [
                new TrueNasCloudSyncTaskDto { Id = 4, Description = "Offsite", Direction = "PUSH", Path = "/mnt/tank/media", Enabled = true, Job = Json("{\"state\":\"FAILED\",\"error\":\"Remote unavailable\"}") }
            ]
        };
        var service = CreateService(client, database, now);

        var result = await service.GetAsync();

        Assert.AreEqual(1, result.FailedTaskCount);
        Assert.IsTrue(result.Warnings.Any(warning => warning.Severity == "danger" && warning.Detail == "Remote unavailable"));
    }

    /// <summary>Verifies an unconfigured connection produces actionable source guidance.</summary>
    [TestMethod]
    [TestCategory("Regression")]
    public async Task GetAsync_MissingTrueNasCredentials_ReturnsConnectionGuidance()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var client = new FakeDataProtectionClient { DatasetException = new InvalidOperationException("TrueNAS username and API key are required.") };
        var service = CreateService(client, database, now);

        var result = await service.GetAsync();

        var datasetSource = result.Sources.Single(source => source.RequiredRole == "DATASET_READ");
        Assert.IsFalse(datasetSource.IsAvailable);
        Assert.AreEqual("Connect TrueNAS in Settings.", datasetSource.Error);
    }

    private static DataProtectionCenterService CreateService(ITrueNasDataProtectionClient client, TestDatabase database, DateTimeOffset now) => new(
        client,
        database.CreateSettingsService(),
        new ScheduleService(new FixedTimeProvider(now)),
        new FixedTimeProvider(now),
        NullLogger<DataProtectionCenterService>.Instance);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FakeDataProtectionClient : ITrueNasDataProtectionClient
    {
        public IReadOnlyList<TrueNasDatasetDto> Datasets { get; init; } = [];
        public Exception? DatasetException { get; init; }
        public IReadOnlyList<TrueNasSnapshotDto> Snapshots { get; init; } = [];
        public Exception? SnapshotException { get; init; }
        public IReadOnlyList<TrueNasSnapshotTaskDto> SnapshotTasks { get; init; } = [];
        public IReadOnlyList<TrueNasReplicationTaskDto> ReplicationTasks { get; init; } = [];
        public IReadOnlyList<TrueNasCloudSyncTaskDto> CloudSyncTasks { get; init; } = [];
        public IReadOnlyList<TrueNasJobDto> Jobs { get; init; } = [];

        public Task<IReadOnlyList<TrueNasDatasetDto>> QueryDatasetsAsync(CancellationToken cancellationToken = default) => DatasetException is null ? Task.FromResult(Datasets) : Task.FromException<IReadOnlyList<TrueNasDatasetDto>>(DatasetException);
        public Task<IReadOnlyList<TrueNasSnapshotDto>> QuerySnapshotsAsync(CancellationToken cancellationToken = default) => SnapshotException is null ? Task.FromResult(Snapshots) : Task.FromException<IReadOnlyList<TrueNasSnapshotDto>>(SnapshotException);
        public Task<IReadOnlyList<TrueNasSnapshotTaskDto>> QuerySnapshotTasksAsync(CancellationToken cancellationToken = default) => Task.FromResult(SnapshotTasks);
        public Task<IReadOnlyList<TrueNasReplicationTaskDto>> QueryReplicationTasksAsync(CancellationToken cancellationToken = default) => Task.FromResult(ReplicationTasks);
        public Task<IReadOnlyList<TrueNasCloudSyncTaskDto>> QueryCloudSyncTasksAsync(CancellationToken cancellationToken = default) => Task.FromResult(CloudSyncTasks);
        public Task<IReadOnlyList<TrueNasJobDto>> ListProtectionJobsAsync(int limit = 500, CancellationToken cancellationToken = default) => Task.FromResult(Jobs);
    }
}
