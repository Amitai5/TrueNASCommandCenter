using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class SystemOverviewTests
{
    /// <summary>Verifies that visible TrueNAS pools map into dashboard health and capacity values.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_VisiblePools_MapsHealthAndCapacity()
    {
        var client = new FakeTrueNasSystemClient
        {
            Pools = [new TrueNasPoolDto { Name = "tank", Status = "ONLINE", Healthy = true, Size = 1_000, Allocated = 250, Free = 750, Fragmentation = "3%" }]
        };
        var service = new StoragePoolOverviewService(client, NullLogger<StoragePoolOverviewService>.Instance);

        var result = await service.GetAsync();

        Assert.IsTrue(result.IsAvailable);
        Assert.HasCount(1, result.Pools);
        Assert.AreEqual("tank", result.Pools[0].Name);
        Assert.AreEqual(25d, result.Pools[0].UsedPercentage);
        Assert.AreEqual("3%", result.Pools[0].Fragmentation);
    }

    /// <summary>Verifies that missing optional pool access does not surface as a dashboard failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_MissingPoolReadRole_ReturnsOptionalPermissionState()
    {
        var client = new FakeTrueNasSystemClient { PoolException = new TrueNasClientException("-32001", "Not authorized") };
        var service = new StoragePoolOverviewService(client, NullLogger<StoragePoolOverviewService>.Instance);

        var result = await service.GetAsync();

        Assert.IsTrue(result.RequiresPoolRead);
        Assert.IsFalse(result.IsAvailable);
        Assert.IsEmpty(result.Pools);
        Assert.IsNull(result.Error);
    }

    /// <summary>Verifies that the shared app statistics stream publishes a current resource snapshot.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task ExecuteAsync_AppStatsEvent_UpdatesSharedResourceSnapshot()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings =>
        {
            settings.TrueNasUsername = "service";
            settings.TrueNasApiKeyEncrypted = protector.Protect("test-key");
        });
        var observedAt = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);
        var client = new FakeTrueNasSystemClient
        {
            Statistics =
            [
                new TrueNasAppStatsDto
                {
                    AppName = "immich",
                    CpuUsage = 12,
                    Memory = 536_870_912,
                    Networks = [new TrueNasAppNetworkStatsDto { ReceiveBytes = 1024, TransmitBytes = 2048 }],
                    BlockIo = new TrueNasAppBlockIoStatsDto { ReadBytes = 4096, WriteBytes = 8192 }
                }
            ]
        };
        using var monitor = new AppResourceMonitorService(client, new SettingsService(database, protector, TestDatabase.TrueNasEndpoint), new FixedTimeProvider(observedAt), NullLogger<AppResourceMonitorService>.Instance);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Updated += updated.SetResult;

        await monitor.StartAsync(CancellationToken.None);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync(CancellationToken.None);

        Assert.IsTrue(monitor.Current.TryGetValue("immich", out var usage));
        Assert.IsNotNull(usage);
        Assert.AreEqual(12, usage.CpuUsagePercent);
        Assert.AreEqual(536_870_912L, usage.MemoryBytes);
        Assert.AreEqual(1024L, usage.NetworkReceiveBytesPerSecond);
        Assert.AreEqual(8192L, usage.BlockWriteBytes);
        Assert.AreEqual(observedAt, usage.ObservedAtUtc);
    }

    private sealed class FakeTrueNasSystemClient : ITrueNasSystemClient
    {
        public IReadOnlyList<TrueNasPoolDto> Pools { get; init; } = [];
        public Exception? PoolException { get; init; }
        public IReadOnlyList<TrueNasAppStatsDto> Statistics { get; init; } = [];

        public Task<IReadOnlyList<TrueNasPoolDto>> QueryPoolsAsync(CancellationToken cancellationToken = default) =>
            PoolException is null ? Task.FromResult(Pools) : Task.FromException<IReadOnlyList<TrueNasPoolDto>>(PoolException);

        public async IAsyncEnumerable<IReadOnlyList<TrueNasAppStatsDto>> WatchAppStatsAsync(int intervalSeconds = 5, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Statistics;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
