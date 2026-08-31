using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class TrueNasPerformanceServiceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetHistoryAsync_AllReportingGraphs_MapsRequestedPerformanceViews()
    {
        var end = new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);
        var start = end.AddHours(-1).ToUnixTimeSeconds();
        var client = new FakePerformanceClient
        {
            Graphs = [new TrueNasPerformanceGraphDto { Name = "interface", Identifiers = ["eno1"] }, new TrueNasPerformanceGraphDto { Name = "disk", Identifiers = ["sda"] }],
            Data =
            [
                Graph("cpu", ["time", "usage"], $"[[{start},25],[{start + 60},35]]"),
                Graph("memory", ["time", "available"], $"[[{start},45000000000],[{start + 60},55000000000]]"),
                Graph("load", ["time", "shortterm", "midterm", "longterm"], $"[[{start},1.2,0.9,0.7]]"),
                Graph("cputemp", ["time", "core0", "core1"], $"[[{start},45,47]]"),
                Graph("interface", ["time", "received", "sent"], $"[[{start},1024,2048]]", "eno1"),
                Graph("disk", ["time", "reads", "writes"], $"[[{start},4096,8192]]", "sda"),
                Graph("arcsize", ["time", "size"], $"[[{start},1073741824]]")
            ]
        };
        var service = new TrueNasPerformanceService(client, new FixedTimeProvider(end), NullLogger<TrueNasPerformanceService>.Instance);

        var result = await service.GetHistoryAsync(SystemPerformanceRange.OneHour);

        Assert.IsFalse(result.RequiresReportingRead);
        Assert.AreEqual(end.AddHours(-1), result.StartUtc);
        Assert.HasCount(7, result.Charts);
        Assert.AreEqual(35, Latest(result, "cpu", "Usage"));
        Assert.AreEqual(55_000_000_000, Latest(result, "memory", "Available"));
        Assert.AreEqual(256_000, Latest(result, "interface", "Sent"));
        Assert.AreEqual(8_388_608, Latest(result, "disk", "Written"));
        Assert.AreEqual(46, Latest(result, "cputemp", "Average"));
        Assert.AreEqual(1.2, Latest(result, "load", "1 min"));
        Assert.IsTrue(client.RequestedGraphs.Any(graph => graph.Name == "interface" && graph.Identifier == "eno1"));
        Assert.IsTrue(client.RequestedGraphs.Any(graph => graph.Name == "disk" && graph.Identifier == "sda"));
    }

    [TestMethod]
    [TestCategory("Regression")]
    public async Task GetHistoryAsync_MissingReportingRead_ReturnsPermissionState()
    {
        var end = new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);
        var client = new FakePerformanceClient { Exception = new TrueNasClientException("EACCES", "REPORTING_READ is required") };
        var service = new TrueNasPerformanceService(client, new FixedTimeProvider(end), NullLogger<TrueNasPerformanceService>.Instance);

        var result = await service.GetHistoryAsync(SystemPerformanceRange.ThirtyDays);

        Assert.IsTrue(result.RequiresReportingRead);
        Assert.IsNull(result.Error);
        Assert.HasCount(7, result.Charts);
    }

    /// <summary>Verifies each selectable history range sends the expected UTC interval to TrueNAS.</summary>
    /// <param name="range">The selected performance history range.</param>
    /// <param name="expectedHours">The expected interval length in hours.</param>
    [TestMethod]
    [DataRow(SystemPerformanceRange.OneHour, 1)]
    [DataRow(SystemPerformanceRange.TwentyFourHours, 24)]
    [DataRow(SystemPerformanceRange.SevenDays, 168)]
    [DataRow(SystemPerformanceRange.ThirtyDays, 720)]
    [TestCategory("Unit")]
    public async Task GetHistoryAsync_SelectedRange_RequestsExpectedUtcInterval(SystemPerformanceRange range, int expectedHours)
    {
        var end = new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);
        var client = new FakePerformanceClient();
        var service = new TrueNasPerformanceService(client, new FixedTimeProvider(end), NullLogger<TrueNasPerformanceService>.Instance);

        var result = await service.GetHistoryAsync(range);

        Assert.AreEqual(range, result.Range);
        Assert.AreEqual(TimeSpan.FromHours(expectedHours), result.EndUtc - result.StartUtc);
        Assert.AreEqual(result.StartUtc, client.RequestedStartUtc);
        Assert.AreEqual(result.EndUtc, client.RequestedEndUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MapRealtime_FullSample_AggregatesHostAndPoolActivity()
    {
        var observedAt = new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);
        var service = new TrueNasPerformanceService(new FakePerformanceClient(), new FixedTimeProvider(observedAt), NullLogger<TrueNasPerformanceService>.Instance);
        var sample = new TrueNasRealtimePerformanceDto
        {
            Cpu = Json("{\"cpu\":{\"usage\":30,\"temp\":44},\"cpu0\":{\"usage\":35,\"temp\":46}}"),
            Memory = Json("{\"physical_memory_total\":1000,\"physical_memory_available\":400,\"arc_size\":250}"),
            Interfaces = Json("{\"eno1\":{\"received_bytes_rate\":100,\"sent_bytes_rate\":50},\"eno2\":{\"received_bytes_rate\":20,\"sent_bytes_rate\":10}}"),
            Disks = Json("{\"sda\":{\"read_bytes\":400,\"write_bytes\":800}}"),
            Zfs = Json("{\"demand_data_hit_percentage\":98.5}"),
            Load = Json("{\"load1\":1.25}"),
            Pools = Json("{\"Main\":{\"read_bytes_rate\":300,\"write_bytes_rate\":500,\"busy\":12}}")
        };

        var result = service.MapRealtime(sample);

        Assert.AreEqual(30, result.CpuUsagePercent);
        Assert.AreEqual(45, result.CpuTemperatureCelsius);
        Assert.AreEqual(600L, result.MemoryUsedBytes);
        Assert.AreEqual(60, result.MemoryUsedPercent);
        Assert.AreEqual(120, result.NetworkReceiveBytesPerSecond);
        Assert.AreEqual(60, result.NetworkSendBytesPerSecond);
        Assert.AreEqual(98.5, result.ArcHitPercent);
        Assert.HasCount(1, result.Pools);
        Assert.AreEqual("Main", result.Pools[0].Name);
    }

    private static double Latest(SystemPerformanceHistory history, string chartKey, string seriesLabel) => history.Charts.Single(chart => chart.Key == chartKey).Series.Single(series => series.Label == seriesLabel).Points.Last().Value;

    private static TrueNasPerformanceDataDto Graph(string name, IReadOnlyList<string> legend, string data, string? identifier = null)
    {
        var payload = Json(data).EnumerateArray().Select(item => item.Clone()).ToList();
        return new TrueNasPerformanceDataDto { Name = name, Identifier = identifier, Legend = legend, Data = payload, Start = payload[0][0].GetInt64(), End = payload[^1][0].GetInt64() };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakePerformanceClient : ITrueNasPerformanceClient
    {
        public IReadOnlyList<TrueNasPerformanceGraphDto> Graphs { get; init; } = [];
        public IReadOnlyList<TrueNasPerformanceDataDto> Data { get; init; } = [];
        public Exception? Exception { get; init; }
        public IReadOnlyList<TrueNasPerformanceGraphRequestDto> RequestedGraphs { get; private set; } = [];
        public DateTimeOffset? RequestedStartUtc { get; private set; }
        public DateTimeOffset? RequestedEndUtc { get; private set; }

        public Task<IReadOnlyList<TrueNasPerformanceGraphDto>> ListPerformanceGraphsAsync(CancellationToken cancellationToken = default) => Exception is null ? Task.FromResult(Graphs) : Task.FromException<IReadOnlyList<TrueNasPerformanceGraphDto>>(Exception);

        public Task<IReadOnlyList<TrueNasPerformanceDataDto>> GetPerformanceDataAsync(IReadOnlyList<TrueNasPerformanceGraphRequestDto> graphs, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
        {
            RequestedGraphs = graphs;
            RequestedStartUtc = startUtc;
            RequestedEndUtc = endUtc;
            return Exception is null ? Task.FromResult(Data) : Task.FromException<IReadOnlyList<TrueNasPerformanceDataDto>>(Exception);
        }

        public async IAsyncEnumerable<TrueNasRealtimePerformanceDto> WatchSystemPerformanceAsync(int intervalSeconds = 5, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
