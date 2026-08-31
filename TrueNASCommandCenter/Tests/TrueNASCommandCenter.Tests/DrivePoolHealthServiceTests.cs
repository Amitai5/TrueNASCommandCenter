using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class DrivePoolHealthServiceTests
{
    /// <summary>Verifies pool scans, vdev errors, drive membership, temperature, and SMART alerts are correlated.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_VisibleStorageData_MapsPoolDriveTemperatureAndWarnings()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeDriveHealthClient
        {
            Pools =
            [
                new TrueNasPoolDto
                {
                    Name = "tank",
                    Status = "ONLINE",
                    Healthy = true,
                    Size = 2_000,
                    Allocated = 500,
                    Scan = new TrueNasPoolScanDto { Function = "SCRUB", State = "SCANNING", Percentage = 32.5, Errors = 0 },
                    Topology = new TrueNasPoolTopologyDto
                    {
                        Data =
                        [
                            new TrueNasVdevDto { Name = "mirror-0", Type = "MIRROR", Status = "ONLINE", Children = [new TrueNasVdevDto { Name = "sda", Disk = "sda", Type = "DISK", Status = "ONLINE", Stats = new TrueNasVdevStatsDto { ReadErrors = 1 } }] }
                        ]
                    }
                }
            ],
            Disks = [new TrueNasDiskDto { Name = "sda", Model = "ExampleDisk", Serial = "SERIAL-1", Size = 2_000, Type = "HDD", Bus = "SATA", RotationRate = 7200, SmartEnabled = true }],
            Temperatures = new Dictionary<string, JsonElement> { ["sda"] = Json("{\"temperature\":58,\"critical\":70}") },
            Alerts = [new TrueNasAlertDto { ClassName = "SMART", Text = "SMART warning for disk sda SERIAL-1", Level = "WARNING", LastOccurrence = now }]
        };
        var service = new DrivePoolHealthService(client, new FixedTimeProvider(now), NullLogger<DrivePoolHealthService>.Instance);

        var result = await service.GetAsync();

        Assert.HasCount(1, result.Pools);
        Assert.AreEqual(1, result.ActiveScanCount);
        Assert.AreEqual(1L, result.Pools[0].TotalVdevErrors);
        Assert.HasCount(1, result.Drives);
        var drive = result.Drives[0];
        Assert.AreEqual("tank", drive.PoolName);
        Assert.AreEqual("Data", drive.VdevGroup);
        Assert.AreEqual(58d, drive.TemperatureCelsius);
        Assert.AreEqual(70d, drive.CriticalTemperatureCelsius);
        Assert.AreEqual("warning", drive.TemperatureState);
        Assert.AreEqual(1L, drive.ReadErrors);
        Assert.AreEqual(1, drive.WarningCount);
        Assert.HasCount(1, result.Warnings);
        Assert.AreEqual(1, result.WarningDriveCount);
    }

    /// <summary>Verifies missing temperature permission preserves pool and drive identity data.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_MissingReportingRead_PreservesOtherStorageSources()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeDriveHealthClient
        {
            Pools = [new TrueNasPoolDto { Name = "tank", Status = "ONLINE", Healthy = true }],
            Disks = [new TrueNasDiskDto { Name = "sda", SmartEnabled = true }],
            TemperatureException = new TrueNasClientException("-32001", "Required role missing")
        };
        var service = new DrivePoolHealthService(client, new FixedTimeProvider(now), NullLogger<DrivePoolHealthService>.Instance);

        var result = await service.GetAsync();

        Assert.HasCount(1, result.Pools);
        Assert.HasCount(1, result.Drives);
        Assert.IsNull(result.Drives[0].TemperatureCelsius);
        var temperatureSource = result.Sources.Single(source => source.RequiredRole == "REPORTING_READ");
        Assert.IsFalse(temperatureSource.IsAvailable);
        StringAssert.Contains(temperatureSource.Error!, "REPORTING_READ");
    }

    /// <summary>Verifies a missing SMART-alert role is isolated from pool, disk, and temperature data.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_MissingAlertListRead_ReturnsExplicitSourceState()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeDriveHealthClient
        {
            Disks = [new TrueNasDiskDto { Name = "nvme0n1", SmartEnabled = true }],
            Temperatures = new Dictionary<string, JsonElement> { ["nvme0n1"] = Json("42") },
            AlertException = new TrueNasClientException("EACCES", "Not authorized")
        };
        var service = new DrivePoolHealthService(client, new FixedTimeProvider(now), NullLogger<DrivePoolHealthService>.Instance);

        var result = await service.GetAsync();

        Assert.HasCount(1, result.Drives);
        Assert.AreEqual(42d, result.Drives[0].TemperatureCelsius);
        Assert.IsEmpty(result.Warnings);
        Assert.IsFalse(result.Sources.Single(source => source.RequiredRole == "ALERT_LIST_READ").IsAvailable);
    }

    /// <summary>Verifies an unconfigured connection produces actionable source guidance.</summary>
    [TestMethod]
    [TestCategory("Regression")]
    public async Task GetAsync_MissingTrueNasCredentials_ReturnsConnectionGuidance()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeDriveHealthClient { PoolException = new InvalidOperationException("TrueNAS username and API key are required.") };
        var service = new DrivePoolHealthService(client, new FixedTimeProvider(now), NullLogger<DrivePoolHealthService>.Instance);

        var result = await service.GetAsync();

        var poolSource = result.Sources.Single(source => source.RequiredRole == "POOL_READ");
        Assert.IsFalse(poolSource.IsAvailable);
        Assert.AreEqual("Connect TrueNAS in Settings.", poolSource.Error);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FakeDriveHealthClient : ITrueNasDriveHealthClient
    {
        public IReadOnlyList<TrueNasPoolDto> Pools { get; init; } = [];
        public Exception? PoolException { get; init; }
        public IReadOnlyList<TrueNasDiskDto> Disks { get; init; } = [];
        public Dictionary<string, JsonElement> Temperatures { get; init; } = [];
        public Exception? TemperatureException { get; init; }
        public IReadOnlyList<TrueNasAlertDto> Alerts { get; init; } = [];
        public Exception? AlertException { get; init; }

        public Task<IReadOnlyList<TrueNasPoolDto>> QueryPoolsAsync(CancellationToken cancellationToken = default) => PoolException is null ? Task.FromResult(Pools) : Task.FromException<IReadOnlyList<TrueNasPoolDto>>(PoolException);
        public Task<IReadOnlyList<TrueNasDiskDto>> QueryDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult(Disks);
        public Task<Dictionary<string, JsonElement>> GetDiskTemperaturesAsync(IReadOnlyList<string> diskNames, CancellationToken cancellationToken = default) => TemperatureException is null ? Task.FromResult(Temperatures) : Task.FromException<Dictionary<string, JsonElement>>(TemperatureException);
        public Task<IReadOnlyList<TrueNasAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default) => AlertException is null ? Task.FromResult(Alerts) : Task.FromException<IReadOnlyList<TrueNasAlertDto>>(AlertException);
    }
}
