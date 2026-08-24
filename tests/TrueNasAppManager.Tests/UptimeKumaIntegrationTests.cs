using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.UptimeKuma;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class UptimeKumaMetricsParserTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void Parse_CurrentExporterMetrics_ReturnsCompleteMonitorReport()
    {
        var content = """
            # HELP monitor_status Monitor Status
            monitor_status{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null"} 1
            monitor_response_time{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null"} 42.5
            monitor_uptime_ratio{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null",window="1d"} 0.999
            monitor_uptime_ratio{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null",window="30d"} 0.987
            monitor_uptime_ratio{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null",window="365d"} 0.975
            monitor_response_time_seconds{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null",window="30d"} 0.125
            monitor_cert_is_valid{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null"} 1
            monitor_cert_days_remaining{monitor_id="7",monitor_name="Plex",monitor_type="http",monitor_url="https://plex.example.test",monitor_hostname="null",monitor_port="null"} 61
            """;
        var parser = new UptimeKumaMetricsParser();

        var result = parser.Parse(content);

        Assert.HasCount(1, result);
        var monitor = result[0];
        Assert.AreEqual("7", monitor.MonitorId);
        Assert.AreEqual("Plex", monitor.Name);
        Assert.AreEqual(UptimeKumaMonitorStatus.Up, monitor.Status);
        Assert.AreEqual(42.5, monitor.ResponseTimeMilliseconds);
        Assert.AreEqual(0.999, monitor.UptimeRatio1Day);
        Assert.AreEqual(0.987, monitor.UptimeRatio30Days);
        Assert.AreEqual(0.975, monitor.UptimeRatio365Days);
        Assert.AreEqual(125, monitor.AverageResponseTimeMilliseconds30Days);
        Assert.IsTrue(monitor.CertificateIsValid);
        Assert.AreEqual(61, monitor.CertificateDaysRemaining);
    }

    [TestMethod]
    [DataRow(0, UptimeKumaMonitorStatus.Down)]
    [DataRow(1, UptimeKumaMonitorStatus.Up)]
    [DataRow(2, UptimeKumaMonitorStatus.Pending)]
    [DataRow(3, UptimeKumaMonitorStatus.Maintenance)]
    [DataRow(9, UptimeKumaMonitorStatus.Unknown)]
    [TestCategory("Unit")]
    public void Parse_MonitorStatusValue_MapsExpectedStatus(int value, UptimeKumaMonitorStatus expected)
    {
        var parser = new UptimeKumaMetricsParser();

        var result = parser.Parse($"monitor_status{{monitor_id=\"1\",monitor_name=\"Monitor\",monitor_type=\"http\"}} {value}");

        Assert.AreEqual(expected, result[0].Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Parse_LegacyMetricsAndEscapedLabels_CreatesStableSyntheticId()
    {
        var parser = new UptimeKumaMetricsParser();
        const string content = "monitor_status{monitor_name=\"API \\\"primary\\\"\",monitor_type=\"tcp\",monitor_hostname=\"server.local\",monitor_port=\"443\"} 0";

        var first = parser.Parse(content)[0];
        var second = parser.Parse(content)[0];

        StringAssert.StartsWith(first.MonitorId, "legacy-");
        Assert.AreEqual(first.MonitorId, second.MonitorId);
        Assert.AreEqual("API \"primary\"", first.Name);
        Assert.AreEqual("server.local", first.Hostname);
        Assert.AreEqual(443, first.Port);
    }
}

[TestClass]
public sealed class UptimeKumaClientTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetMonitorMetricsAsync_ApiKeyConfigured_UsesMetricsEndpointAndBasicPassword()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings =>
        {
            settings.UptimeKumaBaseUrl = "https://kuma.example.test/base/";
            settings.UptimeKumaApiKeyEncrypted = protector.Protect("uk2_test-key");
        });
        var handler = new SequenceHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("monitor_status{monitor_id=\"1\",monitor_name=\"Plex\",monitor_type=\"http\"} 1")
        });
        var client = new UptimeKumaClient(new FakeHttpClientFactory(handler), database.CreateSettingsService(), new UptimeKumaMetricsParser());

        var result = await client.GetMonitorMetricsAsync();

        Assert.HasCount(1, result);
        Assert.AreEqual(new Uri("https://kuma.example.test/base/metrics"), handler.CapturedUris[0]);
        var authorization = handler.CapturedHeaders[0]["Authorization"];
        StringAssert.StartsWith(authorization, "Basic ");
        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..]));
        Assert.AreEqual(":uk2_test-key", credentials);
    }

    [TestMethod]
    [DataRow("ftp://kuma.example.test")]
    [DataRow("https://user:password@kuma.example.test")]
    [DataRow("https://kuma.example.test/?token=secret")]
    [DataRow("not-a-url")]
    [TestCategory("Unit")]
    public void ParseBaseUri_UnsafeOrInvalidAddress_Throws(string value)
    {
        Assert.Throws<InvalidOperationException>(() => UptimeKumaClient.ParseBaseUri(value, "Connection URL"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task TestConnectionAsync_Unauthorized_ReturnsActionableFailure()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => settings.UptimeKumaBaseUrl = "http://kuma.local:3001");
        var handler = new SequenceHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new UptimeKumaClient(new FakeHttpClientFactory(handler), database.CreateSettingsService(), new UptimeKumaMetricsParser());

        var result = await client.TestConnectionAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "Prometheus API key");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetMonitorMetricsAsync_ResponseExceedsSafetyLimit_RejectsResponse()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => settings.UptimeKumaBaseUrl = "http://kuma.local:3001");
        var handler = new SequenceHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[5 * 1024 * 1024 + 1])
        });
        var client = new UptimeKumaClient(new FakeHttpClientFactory(handler), database.CreateSettingsService(), new UptimeKumaMetricsParser());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMonitorMetricsAsync());

        StringAssert.Contains(exception.Message, "5 MB");
    }
}

[TestClass]
public sealed class UptimeKumaSyncServiceTests
{
    private static readonly DateTimeOffset SyncTime = new(2026, 8, 24, 18, 30, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SynchronizeAsync_CurrentMetrics_UpdatesCacheAndPreservesMissingMonitorMapping()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => { settings.UptimeKumaEnabled = true; settings.UptimeKumaBaseUrl = "http://kuma.local:3001/"; });
        await using (var seed = await database.CreateDbContextAsync())
        {
            seed.Apps.Add(new AppRecord { Id = "plex", Name = "Plex", LastSeenUtc = SyncTime.UtcDateTime });
            seed.UptimeKumaMonitors.Add(new UptimeKumaMonitorRecord { MonitorId = "old", AppId = "plex", Name = "Old monitor", Type = "http", Status = UptimeKumaMonitorStatus.Up, IsPresent = true, LastSeenUtc = SyncTime.AddDays(-1).UtcDateTime });
            await seed.SaveChangesAsync();
        }
        var metric = new UptimeKumaMonitorMetric("7", "Plex", "http", "https://plex.example.test", null, null, UptimeKumaMonitorStatus.Up, 35, 1, 0.99, 0.98, 35, 40, 45, true, 90);
        var service = new UptimeKumaSyncService(database, new FakeUptimeKumaClient([metric]), new FixedTimeProvider(SyncTime), NullLogger<UptimeKumaSyncService>.Instance);

        var result = await service.SynchronizeAsync();

        Assert.IsTrue(result.Success);
        await using var verify = await database.CreateDbContextAsync();
        var imported = await verify.UptimeKumaMonitors.SingleAsync(monitor => monitor.MonitorId == "7");
        Assert.AreEqual(UptimeKumaMonitorStatus.Up, imported.Status);
        Assert.AreEqual(0.99, imported.UptimeRatio30Days);
        var missing = await verify.UptimeKumaMonitors.SingleAsync(monitor => monitor.MonitorId == "old");
        Assert.IsFalse(missing.IsPresent);
        Assert.AreEqual("plex", missing.AppId);
        var settings = await verify.Settings.SingleAsync();
        Assert.AreEqual(SyncTime.UtcDateTime, settings.LastUptimeKumaSuccessUtc);
        Assert.IsNull(settings.LastUptimeKumaError);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SynchronizeAsync_RequestFails_RetainsCachedStatusAndRecordsStaleError()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => { settings.UptimeKumaEnabled = true; settings.UptimeKumaBaseUrl = "http://kuma.local:3001/"; });
        await using (var seed = await database.CreateDbContextAsync())
        {
            seed.UptimeKumaMonitors.Add(new UptimeKumaMonitorRecord { MonitorId = "7", Name = "Plex", Type = "http", Status = UptimeKumaMonitorStatus.Up, IsPresent = true, LastSeenUtc = SyncTime.AddMinutes(-1).UtcDateTime });
            await seed.SaveChangesAsync();
        }
        var service = new UptimeKumaSyncService(database, new FakeUptimeKumaClient(new HttpRequestException("Kuma unavailable")), new FixedTimeProvider(SyncTime), NullLogger<UptimeKumaSyncService>.Instance);

        var result = await service.SynchronizeAsync();

        Assert.IsFalse(result.Success);
        await using var verify = await database.CreateDbContextAsync();
        Assert.AreEqual(UptimeKumaMonitorStatus.Up, (await verify.UptimeKumaMonitors.SingleAsync()).Status);
        StringAssert.Contains((await verify.Settings.SingleAsync()).LastUptimeKumaError!, "unavailable");
    }

    private sealed class FakeUptimeKumaClient : IUptimeKumaClient
    {
        private readonly IReadOnlyList<UptimeKumaMonitorMetric>? metrics;
        private readonly Exception? exception;

        public FakeUptimeKumaClient(IReadOnlyList<UptimeKumaMonitorMetric> metrics) => this.metrics = metrics;
        public FakeUptimeKumaClient(Exception exception) => this.exception = exception;

        public Task<UptimeKumaConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UptimeKumaConnectionTestResult(true, "Connected", metrics?.Count ?? 0));

        public Task<IReadOnlyList<UptimeKumaMonitorMetric>> GetMonitorMetricsAsync(CancellationToken cancellationToken = default) => exception is null ? Task.FromResult(metrics ?? []) : Task.FromException<IReadOnlyList<UptimeKumaMonitorMetric>>(exception);
    }
}
