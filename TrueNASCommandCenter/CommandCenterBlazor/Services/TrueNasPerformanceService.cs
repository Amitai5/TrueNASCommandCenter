using System.Globalization;
using System.Text.Json;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

/// <summary>Loads and maps historical TrueNAS host performance data.</summary>
public interface ITrueNasPerformanceService
{
    /// <summary>Returns display-ready performance charts for the selected historical range.</summary>
    /// <param name="range">The historical range to request.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The historical performance snapshot.</returns>
    Task<SystemPerformanceHistory> GetHistoryAsync(SystemPerformanceRange range, CancellationToken cancellationToken = default);
}

/// <summary>Maps TrueNAS reporting data into stable, display-ready system performance models.</summary>
public sealed class TrueNasPerformanceService(ITrueNasPerformanceClient trueNasClient, TimeProvider timeProvider, ILogger<TrueNasPerformanceService> logger) : ITrueNasPerformanceService
{
    private const int MaximumChartPoints = 180;
    private static readonly string[] BaseGraphNames = ["cpu", "cputemp", "load", "memory", "arcsize"];

    /// <inheritdoc />
    public async Task<SystemPerformanceHistory> GetHistoryAsync(SystemPerformanceRange range, CancellationToken cancellationToken = default)
    {
        var endUtc = timeProvider.GetUtcNow();
        var startUtc = endUtc - RangeDuration(range);
        try
        {
            var graphRequests = await BuildGraphRequestsAsync(cancellationToken);
            var rawData = await trueNasClient.GetPerformanceDataAsync(graphRequests, startUtc, endUtc, cancellationToken);
            return new SystemPerformanceHistory(range, startUtc, endUtc, BuildCharts(rawData));
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("TrueNAS performance history is unavailable because the API account does not have REPORTING_READ");
            return new SystemPerformanceHistory(range, startUtc, endUtc, EmptyCharts(), RequiresReportingRead: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TrueNAS performance history could not be loaded");
            return new SystemPerformanceHistory(range, startUtc, endUtc, EmptyCharts(), Error: "Performance history is temporarily unavailable.");
        }
    }

    /// <summary>Maps one raw TrueNAS realtime event into a display-safe performance sample.</summary>
    /// <param name="source">The raw realtime reporting event.</param>
    /// <returns>The aggregated host and pool performance sample.</returns>
    public LiveSystemPerformance MapRealtime(TrueNasRealtimePerformanceDto source)
    {
        var cpuObjects = ChildObjects(source.Cpu).ToList();
        var aggregateCpu = cpuObjects.FirstOrDefault(item => string.Equals(item.Name, "cpu", StringComparison.OrdinalIgnoreCase)).Values ?? cpuObjects.FirstOrDefault().Values;
        var cpuUsage = aggregateCpu is null ? null : ReadCpuUsage(aggregateCpu);
        var cpuTemperature = Average(cpuObjects.Select(item => FindMetric(item.Values, "temp", "temperature")).Where(value => value is not null).Select(value => value!.Value));

        var memory = NumericProperties(source.Memory);
        var totalMemory = FindMetric(memory, "physical_memory_total", "total");
        var availableMemory = FindMetric(memory, "physical_memory_available", "available", "free");
        var usedMemory = totalMemory is null || availableMemory is null ? FindMetric(memory, "used") : Math.Max(0, totalMemory.Value - availableMemory.Value);
        double? memoryPercent = totalMemory is > 0 && usedMemory is not null ? usedMemory.Value / totalMemory.Value * 100 : null;

        var networkChildren = ChildObjects(source.Interfaces).Select(item => item.Values).ToList();
        var networkReceive = SumMetrics(networkChildren, "received_bytes_rate", "receive_bytes_rate", "received", "rx");
        var networkSend = SumMetrics(networkChildren, "sent_bytes_rate", "transmit_bytes_rate", "sent", "tx");

        var diskChildren = ChildObjects(source.Disks).Select(item => item.Values).ToList();
        if (diskChildren.Count == 0 && source.Disks.ValueKind == JsonValueKind.Object)
        {
            diskChildren.Add(NumericProperties(source.Disks));
        }

        var diskRead = SumMetrics(diskChildren, "read_bytes", "reads", "read");
        var diskWrite = SumMetrics(diskChildren, "write_bytes", "writes", "write");
        var zfs = NumericProperties(source.Zfs);
        var arcSize = FindMetric(memory, "arc_size") ?? FindMetric(zfs, "arc_size", "size");
        var arcHit = ReadHitPercent(zfs);
        var load = NumericProperties(source.Load);

        var pools = ChildObjects(source.Pools)
            .Select(pool => new LivePoolActivity(
                pool.Name,
                FindMetric(pool.Values, "read_bytes_rate", "read_bytes", "reads", "read") ?? 0,
                FindMetric(pool.Values, "write_bytes_rate", "write_bytes", "writes", "write") ?? 0,
                FindMetric(pool.Values, "busy", "busy_percent", "utilization")))
            .OrderBy(pool => pool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LiveSystemPerformance(
            timeProvider.GetUtcNow(),
            ClampPercent(cpuUsage),
            cpuTemperature,
            ToLong(usedMemory),
            ToLong(totalMemory),
            ClampPercent(memoryPercent),
            FindMetric(load, "load1", "one", "1m"),
            Math.Max(0, networkReceive),
            Math.Max(0, networkSend),
            Math.Max(0, diskRead),
            Math.Max(0, diskWrite),
            ToLong(arcSize),
            ClampPercent(arcHit),
            pools);
    }

    private async Task<IReadOnlyList<TrueNasPerformanceGraphRequestDto>> BuildGraphRequestsAsync(CancellationToken cancellationToken)
    {
        var requests = BaseGraphNames.Select(name => new TrueNasPerformanceGraphRequestDto(name)).ToList();
        try
        {
            var graphs = await trueNasClient.ListPerformanceGraphsAsync(cancellationToken);
            foreach (var graph in graphs.Where(graph => graph.Name is "interface" or "disk"))
            {
                foreach (var identifier in graph.Identifiers?.Where(identifier => !string.IsNullOrWhiteSpace(identifier)).Take(64) ?? [])
                {
                    requests.Add(new TrueNasPerformanceGraphRequestDto(graph.Name, identifier));
                }
            }
        }
        catch (TrueNasClientException exception) when (!IsPermissionFailure(exception))
        {
            logger.LogDebug(exception, "TrueNAS reporting graph discovery was unavailable; aggregate graph requests will be used");
        }

        return requests.DistinctBy(request => $"{request.Name}\0{request.Identifier}", StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<SystemPerformanceChart> BuildCharts(IReadOnlyList<TrueNasPerformanceDataDto> rawData)
    {
        var parsed = rawData.Select(data => new ParsedGraph(data.Name, data.Identifier, ParsePoints(data))).ToList();
        return
        [
            SingleSeriesChart("cpu", "CPU usage", SystemPerformanceUnit.Percent, parsed, "Usage", point => ClampPercent(ReadCpuUsage(point.Values))),
            SingleSeriesChart("memory", "Memory available", SystemPerformanceUnit.Bytes, parsed, "Available", point => FindMetric(point.Values, "available", "physical_memory_available", "value") ?? Average(point.Values.Values)),
            MultiSeriesChart("load", "System load", SystemPerformanceUnit.Load, parsed, [("1 min", new[] { "shortterm", "load1", "one", "1m" }), ("5 min", new[] { "midterm", "load5", "five", "5m" }), ("15 min", new[] { "longterm", "load15", "fifteen", "15m" })]),
            SingleSeriesChart("cputemp", "CPU temperature", SystemPerformanceUnit.Celsius, parsed, "Average", point => Average(point.Values.Values)),
            AggregateChart("interface", "Network throughput", SystemPerformanceUnit.BytesPerSecond, parsed, [("Received", new[] { "received_bytes_rate", "receive_bytes_rate", "received", "rx" }), ("Sent", new[] { "sent_bytes_rate", "transmit_bytes_rate", "sent", "tx" })], 125),
            AggregateChart("disk", "Disk I/O", SystemPerformanceUnit.BytesPerSecond, parsed, [("Read", new[] { "read_bytes", "reads", "read" }), ("Written", new[] { "write_bytes", "writes", "write" })], 1024),
            SingleSeriesChart("arcsize", "ZFS ARC size", SystemPerformanceUnit.Bytes, parsed, "ARC", point => FindMetric(point.Values, "arc_size", "size", "value") ?? Average(point.Values.Values))
        ];
    }

    private static IReadOnlyList<SystemPerformanceChart> EmptyCharts() => BuildCharts([]);

    private static SystemPerformanceChart SingleSeriesChart(string key, string title, SystemPerformanceUnit unit, IReadOnlyList<ParsedGraph> graphs, string label, Func<MetricPoint, double?> selector)
    {
        var points = graphs.Where(graph => string.Equals(graph.Name, key, StringComparison.OrdinalIgnoreCase)).SelectMany(graph => graph.Points).Select(point => (point.TimestampUtc, Value: selector(point))).Where(point => point.Value is not null).Select(point => new SystemPerformancePoint(point.TimestampUtc, point.Value!.Value)).OrderBy(point => point.TimestampUtc).ToList();
        return new SystemPerformanceChart(key, title, unit, [new SystemPerformanceSeries(label, Downsample(points))]);
    }

    private static SystemPerformanceChart MultiSeriesChart(string key, string title, SystemPerformanceUnit unit, IReadOnlyList<ParsedGraph> graphs, IReadOnlyList<(string Label, string[] Fields)> definitions)
    {
        var source = graphs.Where(graph => string.Equals(graph.Name, key, StringComparison.OrdinalIgnoreCase)).SelectMany(graph => graph.Points).ToList();
        var series = definitions.Select(definition => new SystemPerformanceSeries(definition.Label, Downsample(source.Select(point => (point.TimestampUtc, Value: FindMetric(point.Values, definition.Fields))).Where(point => point.Value is not null).Select(point => new SystemPerformancePoint(point.TimestampUtc, point.Value!.Value)).OrderBy(point => point.TimestampUtc).ToList()))).ToList();
        return new SystemPerformanceChart(key, title, unit, series);
    }

    private static SystemPerformanceChart AggregateChart(string key, string title, SystemPerformanceUnit unit, IReadOnlyList<ParsedGraph> graphs, IReadOnlyList<(string Label, string[] Fields)> definitions, double scale)
    {
        var source = graphs.Where(graph => string.Equals(graph.Name, key, StringComparison.OrdinalIgnoreCase)).SelectMany(graph => graph.Points).ToList();
        var series = definitions.Select(definition =>
        {
            var points = source.GroupBy(point => point.TimestampUtc).Select(group => new SystemPerformancePoint(group.Key, group.Sum(point => Math.Max(0, FindMetric(point.Values, definition.Fields) ?? 0)) * scale)).OrderBy(point => point.TimestampUtc).ToList();
            return new SystemPerformanceSeries(definition.Label, Downsample(points));
        }).ToList();
        return new SystemPerformanceChart(key, title, unit, series);
    }

    private static IReadOnlyList<MetricPoint> ParsePoints(TrueNasPerformanceDataDto data)
    {
        var points = new List<MetricPoint>(data.Data.Count);
        for (var index = 0; index < data.Data.Count; index++)
        {
            var values = ReadPointValues(data.Data[index], data.Legend);
            var timestamp = ReadTimestamp(values) ?? InterpolateTimestamp(data, index);
            values.Remove("time");
            values.Remove("timestamp");
            points.Add(new MetricPoint(timestamp, values));
        }

        return points;
    }

    private static Dictionary<string, double> ReadPointValues(JsonElement point, IReadOnlyList<string> legend)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (point.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in point.EnumerateObject())
            {
                if (TryReadDouble(property.Value, out var value))
                {
                    values[property.Name] = value;
                }
            }
        }
        else if (point.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var valueElement in point.EnumerateArray())
            {
                if (index < legend.Count && TryReadDouble(valueElement, out var value))
                {
                    values[legend[index]] = value;
                }

                index++;
            }
        }

        return values;
    }

    private static DateTimeOffset? ReadTimestamp(IReadOnlyDictionary<string, double> values)
    {
        var value = FindMetric(values, "time", "timestamp");
        if (value is null || value <= 0)
        {
            return null;
        }

        return value > 9_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value) : DateTimeOffset.FromUnixTimeSeconds((long)value.Value);
    }

    private static DateTimeOffset InterpolateTimestamp(TrueNasPerformanceDataDto data, int index)
    {
        var start = data.Start > 0 ? DateTimeOffset.FromUnixTimeSeconds(data.Start) : DateTimeOffset.UnixEpoch;
        if (data.Data.Count < 2 || data.End <= data.Start)
        {
            return start.AddSeconds(index);
        }

        return start.AddSeconds((data.End - data.Start) * index / (double)(data.Data.Count - 1));
    }

    private static Dictionary<string, double> NumericProperties(JsonElement element)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (TryReadDouble(property.Value, out var value))
            {
                result[property.Name] = value;
            }
        }

        return result;
    }

    private static IEnumerable<(string Name, Dictionary<string, double> Values)> ChildObjects(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                yield return (property.Name, NumericProperties(property.Value));
            }
        }
    }

    private static double? ReadCpuUsage(IReadOnlyDictionary<string, double> values)
    {
        var usage = FindMetric(values, "usage", "used");
        if (usage is not null)
        {
            return usage;
        }

        var idle = FindMetric(values, "idle");
        return idle is null ? null : 100 - idle;
    }

    private static double? ReadHitPercent(IReadOnlyDictionary<string, double> values)
    {
        var percent = FindMetric(values, "demand_data_hit_percentage", "hit_percentage", "hit_ratio", "ratio");
        if (percent is not null)
        {
            return percent <= 1 ? percent * 100 : percent;
        }

        var hits = FindMetric(values, "hits", "hit");
        var misses = FindMetric(values, "misses", "miss");
        return hits is not null && misses is not null && hits + misses > 0 ? hits / (hits + misses) * 100 : null;
    }

    private static double SumMetrics(IEnumerable<IReadOnlyDictionary<string, double>> sources, params string[] names) => sources.Sum(source => Math.Max(0, FindMetric(source, names) ?? 0));

    private static double? FindMetric(IReadOnlyDictionary<string, double> values, params string[] names)
    {
        foreach (var name in names)
        {
            var match = values.FirstOrDefault(value => string.Equals(NormalizeMetricName(value.Key), NormalizeMetricName(name), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                return match.Value;
            }
        }

        foreach (var name in names)
        {
            var normalized = NormalizeMetricName(name);
            var match = values.FirstOrDefault(value => NormalizeMetricName(value.Key).Contains(normalized, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string NormalizeMetricName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
        {
            return double.IsFinite(result);
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    }

    private static IReadOnlyList<SystemPerformancePoint> Downsample(IReadOnlyList<SystemPerformancePoint> points)
    {
        if (points.Count <= MaximumChartPoints)
        {
            return points;
        }

        var step = (int)Math.Ceiling(points.Count / (double)MaximumChartPoints);
        var sampled = points.Where((_, index) => index % step == 0).ToList();
        if (sampled[^1] != points[^1])
        {
            sampled.Add(points[^1]);
        }

        return sampled;
    }

    private static TimeSpan RangeDuration(SystemPerformanceRange range) => range switch
    {
        SystemPerformanceRange.OneHour => TimeSpan.FromHours(1),
        SystemPerformanceRange.TwentyFourHours => TimeSpan.FromHours(24),
        SystemPerformanceRange.SevenDays => TimeSpan.FromDays(7),
        SystemPerformanceRange.ThirtyDays => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Unsupported performance range.")
    };

    private static bool IsPermissionFailure(TrueNasClientException exception) => exception.Code is "-32001" or "EACCES" or "EPERM" || exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("authorized", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("role", StringComparison.OrdinalIgnoreCase);
    private static double? ClampPercent(double? value) => value is null ? null : Math.Clamp(value.Value, 0, 100);
    private static double? Average(IEnumerable<double> values) { var list = values.ToList(); return list.Count == 0 ? null : list.Average(); }
    private static long? ToLong(double? value) => value is null ? null : (long)Math.Clamp(value.Value, 0, long.MaxValue);

    private sealed record MetricPoint(DateTimeOffset TimestampUtc, Dictionary<string, double> Values);
    private sealed record ParsedGraph(string Name, string? Identifier, IReadOnlyList<MetricPoint> Points);
}
