using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class SystemOverviewTests
{
    /// <summary>Verifies that independent TrueNAS system capabilities map into one ordered read-only overview.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_AllCapabilities_MapsHostUpdatesAlertsAndStorage()
    {
        var client = new FakeTrueNasSystemClient
        {
            SystemInfo = new TrueNasSystemInfoDto
            {
                Hostname = "atlas",
                Version = "25.10.1",
                CpuModel = "Example CPU",
                PhysicalMemory = 68_719_476_736,
                CoreCount = 16,
                PhysicalCoreCount = 8,
                LoadAverage = [0.25, 0.5, 0.75],
                Uptime = "4 days, 2 hours",
                BootTime = new DateTimeOffset(2026, 8, 23, 16, 0, 0, TimeSpan.Zero),
                TimeZoneId = "America/Los_Angeles",
                SystemManufacturer = "Example Systems",
                SystemProduct = "Storage Server",
                HasEccMemory = true
            },
            UpdateStatus = new TrueNasUpdateStatusDto
            {
                Code = "NORMAL",
                Status = new TrueNasUpdateStatusDetailsDto
                {
                    CurrentVersion = new TrueNasCurrentVersionDto { Train = "TrueNAS-SCALE-25.10", Profile = "GENERAL", MatchesProfile = true },
                    NewVersion = new TrueNasAvailableVersionDto { Version = "25.10.2", ReleaseNotes = "Reliability update", ReleaseNotesUrl = "https://www.truenas.com/docs/release-notes/" }
                },
                DownloadProgress = new TrueNasUpdateDownloadProgressDto { Percent = 42.5, Description = "Downloading update", Version = "25.10.2" }
            },
            Alerts =
            [
                new TrueNasAlertDto { Uuid = "dismissed", Text = "Old alert", Level = "CRITICAL", IsDismissed = true, LastOccurrence = new DateTimeOffset(2026, 8, 27, 17, 0, 0, TimeSpan.Zero) },
                new TrueNasAlertDto { Uuid = "warning", Text = "Pool usage is high", Level = "WARNING", LastOccurrence = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.Zero) },
                new TrueNasAlertDto { Uuid = "critical", Source = "Disk", ClassName = "Smartd", Node = "atlas", Text = "Disk fault detected", Level = "critical", LastOccurrence = new DateTimeOffset(2026, 8, 27, 16, 0, 0, TimeSpan.Zero) }
            ],
            Pools = [new TrueNasPoolDto { Name = "tank", Status = "ONLINE", Healthy = true, Size = 1_000, Allocated = 250, Free = 750 }]
        };
        var poolService = new StoragePoolOverviewService(client, NullLogger<StoragePoolOverviewService>.Instance);
        var service = new TrueNasSystemOverviewService(client, poolService, NullLogger<TrueNasSystemOverviewService>.Instance);

        var result = await service.GetAsync();

        Assert.IsTrue(result.Host.IsAvailable);
        Assert.AreEqual("atlas", result.Host.Information!.Hostname);
        Assert.AreEqual(0.5, result.Host.Information.LoadAverageFiveMinutes);
        Assert.IsTrue(result.Update.IsAvailable);
        Assert.IsTrue(result.Update.Information!.IsUpdateAvailable);
        Assert.IsTrue(result.Update.Information.IsDownloading);
        Assert.AreEqual("25.10.2", result.Update.Information.AvailableVersion);
        Assert.AreEqual(42.5, result.Update.Information.DownloadPercent);
        Assert.IsTrue(result.Alerts.IsAvailable);
        Assert.AreEqual(2, result.Alerts.ActiveCount);
        Assert.AreEqual(1, result.Alerts.CriticalCount);
        CollectionAssert.AreEqual(new[] { "critical", "warning", "dismissed" }, result.Alerts.Alerts.Select(alert => alert.Id).ToArray());
        Assert.IsTrue(result.Storage.IsAvailable);
        Assert.HasCount(1, result.Storage.Pools);
    }

    /// <summary>Verifies that missing optional roles are reported independently rather than failing the whole System page.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_MissingOptionalRoles_ReturnsIndependentPermissionStates()
    {
        var client = new FakeTrueNasSystemClient
        {
            SystemInfoException = new TrueNasClientException("-32001", "Not authorized"),
            UpdateStatusException = new TrueNasClientException("EACCES", "Permission denied"),
            AlertException = new TrueNasClientException("EPERM", "Required role missing"),
            PoolException = new TrueNasClientException("-32001", "Not authorized")
        };
        var poolService = new StoragePoolOverviewService(client, NullLogger<StoragePoolOverviewService>.Instance);
        var service = new TrueNasSystemOverviewService(client, poolService, NullLogger<TrueNasSystemOverviewService>.Instance);

        var result = await service.GetAsync();

        Assert.IsTrue(result.Host.RequiresReadOnlyAdmin);
        Assert.IsTrue(result.Update.RequiresSystemUpdateRead);
        Assert.IsTrue(result.Alerts.RequiresAlertListRead);
        Assert.IsTrue(result.Storage.RequiresPoolRead);
        Assert.IsFalse(result.Host.IsAvailable);
        Assert.IsFalse(result.Update.IsAvailable);
        Assert.IsFalse(result.Alerts.IsAvailable);
        Assert.IsFalse(result.Storage.IsAvailable);
    }

    /// <summary>Verifies that a provider-level update check error remains visible without hiding other system data.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_UpdateProviderError_ReturnsReadableCheckError()
    {
        var client = new FakeTrueNasSystemClient
        {
            UpdateStatus = new TrueNasUpdateStatusDto
            {
                Code = "ERROR",
                Error = new TrueNasUpdateErrorDto { ErrorName = "ENONET", Reason = "Update server unavailable" }
            }
        };
        var poolService = new StoragePoolOverviewService(client, NullLogger<StoragePoolOverviewService>.Instance);
        var service = new TrueNasSystemOverviewService(client, poolService, NullLogger<TrueNasSystemOverviewService>.Instance);

        var result = await service.GetAsync();

        Assert.IsTrue(result.Update.IsAvailable);
        Assert.AreEqual("Update server unavailable", result.Update.Information!.CheckError);
        Assert.IsFalse(result.Update.Information.IsUpdateAvailable);
        Assert.IsTrue(result.Alerts.IsAvailable);
    }

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
        public TrueNasSystemInfoDto SystemInfo { get; init; } = new();
        public Exception? SystemInfoException { get; init; }
        public TrueNasUpdateStatusDto UpdateStatus { get; init; } = new() { Code = "NORMAL", Status = new TrueNasUpdateStatusDetailsDto() };
        public Exception? UpdateStatusException { get; init; }
        public IReadOnlyList<TrueNasAlertDto> Alerts { get; init; } = [];
        public Exception? AlertException { get; init; }
        public IReadOnlyList<TrueNasPoolDto> Pools { get; init; } = [];
        public Exception? PoolException { get; init; }
        public IReadOnlyList<TrueNasAppStatsDto> Statistics { get; init; } = [];

        public Task<TrueNasSystemInfoDto> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
            SystemInfoException is null ? Task.FromResult(SystemInfo) : Task.FromException<TrueNasSystemInfoDto>(SystemInfoException);

        public Task<IReadOnlyList<TrueNasAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default) =>
            AlertException is null ? Task.FromResult(Alerts) : Task.FromException<IReadOnlyList<TrueNasAlertDto>>(AlertException);

        public Task<TrueNasUpdateStatusDto> GetUpdateStatusAsync(CancellationToken cancellationToken = default) =>
            UpdateStatusException is null ? Task.FromResult(UpdateStatus) : Task.FromException<TrueNasUpdateStatusDto>(UpdateStatusException);

        public Task<IReadOnlyList<TrueNasPoolDto>> QueryPoolsAsync(CancellationToken cancellationToken = default) =>
            PoolException is null ? Task.FromResult(Pools) : Task.FromException<IReadOnlyList<TrueNasPoolDto>>(PoolException);

        public async IAsyncEnumerable<IReadOnlyList<TrueNasAppStatsDto>> WatchAppStatsAsync(int intervalSeconds = 5, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Statistics;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
