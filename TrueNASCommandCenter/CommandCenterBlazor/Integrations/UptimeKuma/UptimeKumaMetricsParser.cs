using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Integrations.UptimeKuma;

public sealed class UptimeKumaMetricsParser
{
    private static readonly HashSet<string> SupportedMetrics =
    [
        "monitor_status",
        "monitor_response_time",
        "monitor_uptime_ratio",
        "monitor_response_time_seconds",
        "monitor_cert_is_valid",
        "monitor_cert_days_remaining"
    ];

    /// <summary>Parses the supported Uptime Kuma Prometheus metrics into monitor snapshots.</summary>
    /// <param name="content">The Prometheus text exposition returned by Uptime Kuma.</param>
    /// <returns>The latest metric values grouped by stable monitor ID.</returns>
    public IReadOnlyList<UptimeKumaMonitorMetric> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var monitors = new Dictionary<string, MutableMonitor>(StringComparer.Ordinal);
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (!TryParseSample(line, out var metricName, out var labels, out var value) || !SupportedMetrics.Contains(metricName))
            {
                continue;
            }

            var monitorId = GetMonitorId(labels);
            if (!monitors.TryGetValue(monitorId, out var monitor))
            {
                monitor = new MutableMonitor(monitorId);
                monitors.Add(monitorId, monitor);
            }

            monitor.UpdateIdentity(labels);
            monitor.UpdateMetric(metricName, labels.GetValueOrDefault("window"), value);
        }

        return monitors.Values.Select(monitor => monitor.ToMetric()).OrderBy(monitor => monitor.Name, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.MonitorId, StringComparer.Ordinal).ToList();
    }

    private static bool TryParseSample(string line, out string metricName, out Dictionary<string, string> labels, out double value)
    {
        metricName = string.Empty;
        labels = new Dictionary<string, string>(StringComparer.Ordinal);
        value = 0;

        var span = line.AsSpan().Trim();
        if (span.IsEmpty || span[0] == '#')
        {
            return false;
        }

        var labelStart = span.IndexOf('{');
        var valueStart = -1;
        if (labelStart >= 0)
        {
            var labelEndOffset = span[(labelStart + 1)..].IndexOf('}');
            if (labelEndOffset < 0)
            {
                return false;
            }

            var labelEnd = labelStart + 1 + labelEndOffset;
            metricName = span[..labelStart].ToString();
            if (!TryParseLabels(span[(labelStart + 1)..labelEnd], labels))
            {
                return false;
            }

            valueStart = labelEnd + 1;
        }
        else
        {
            var separator = span.IndexOfAny(' ', '\t');
            if (separator < 0)
            {
                return false;
            }

            metricName = span[..separator].ToString();
            valueStart = separator;
        }

        var valueText = span[valueStart..].Trim();
        var trailingSeparator = valueText.IndexOfAny(' ', '\t');
        if (trailingSeparator >= 0)
        {
            valueText = valueText[..trailingSeparator];
        }

        return double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool TryParseLabels(ReadOnlySpan<char> span, Dictionary<string, string> labels)
    {
        var index = 0;
        while (index < span.Length)
        {
            while (index < span.Length && (span[index] == ',' || char.IsWhiteSpace(span[index])))
            {
                index++;
            }

            if (index >= span.Length)
            {
                return true;
            }

            var nameStart = index;
            while (index < span.Length && span[index] != '=')
            {
                index++;
            }

            if (index >= span.Length)
            {
                return false;
            }

            var name = span[nameStart..index].Trim().ToString();
            index++;
            if (index >= span.Length || span[index] != '"')
            {
                return false;
            }

            index++;
            var value = new StringBuilder();
            var closed = false;
            while (index < span.Length)
            {
                var character = span[index++];
                if (character == '"')
                {
                    closed = true;
                    break;
                }

                if (character == '\\' && index < span.Length)
                {
                    var escaped = span[index++];
                    value.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => escaped
                    });
                }
                else
                {
                    value.Append(character);
                }
            }

            if (!closed || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            labels[name] = value.ToString();
        }

        return true;
    }

    private static string GetMonitorId(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.TryGetValue("monitor_id", out var monitorId) && !string.IsNullOrWhiteSpace(monitorId))
        {
            return monitorId;
        }

        var identity = string.Join('|', labels.GetValueOrDefault("monitor_name"), labels.GetValueOrDefault("monitor_type"), labels.GetValueOrDefault("monitor_url"), labels.GetValueOrDefault("monitor_hostname"), labels.GetValueOrDefault("monitor_port"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"legacy-{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private sealed class MutableMonitor(string monitorId)
    {
        public string MonitorId { get; } = monitorId;
        public string Name { get; private set; } = monitorId;
        public string Type { get; private set; } = "unknown";
        public string? Url { get; private set; }
        public string? Hostname { get; private set; }
        public int? Port { get; private set; }
        public UptimeKumaMonitorStatus Status { get; private set; } = UptimeKumaMonitorStatus.Unknown;
        public double? ResponseTimeMilliseconds { get; private set; }
        public double? UptimeRatio1Day { get; private set; }
        public double? UptimeRatio30Days { get; private set; }
        public double? UptimeRatio365Days { get; private set; }
        public double? AverageResponseTimeMilliseconds1Day { get; private set; }
        public double? AverageResponseTimeMilliseconds30Days { get; private set; }
        public double? AverageResponseTimeMilliseconds365Days { get; private set; }
        public bool? CertificateIsValid { get; private set; }
        public double? CertificateDaysRemaining { get; private set; }

        public void UpdateIdentity(IReadOnlyDictionary<string, string> labels)
        {
            Name = NullIfPrometheusNull(labels.GetValueOrDefault("monitor_name")) ?? Name;
            Type = NullIfPrometheusNull(labels.GetValueOrDefault("monitor_type")) ?? Type;
            Url = NullIfPrometheusNull(labels.GetValueOrDefault("monitor_url"));
            Hostname = NullIfPrometheusNull(labels.GetValueOrDefault("monitor_hostname"));
            Port = int.TryParse(NullIfPrometheusNull(labels.GetValueOrDefault("monitor_port")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null;
        }

        public void UpdateMetric(string metricName, string? window, double value)
        {
            switch (metricName)
            {
                case "monitor_status": Status = value switch { 0 => UptimeKumaMonitorStatus.Down, 1 => UptimeKumaMonitorStatus.Up, 2 => UptimeKumaMonitorStatus.Pending, 3 => UptimeKumaMonitorStatus.Maintenance, _ => UptimeKumaMonitorStatus.Unknown }; break;
                case "monitor_response_time": ResponseTimeMilliseconds = value >= 0 ? value : null; break;
                case "monitor_cert_is_valid": CertificateIsValid = value >= 1; break;
                case "monitor_cert_days_remaining": CertificateDaysRemaining = value; break;
                case "monitor_uptime_ratio": SetWindowValue(window, value, ratio: true); break;
                case "monitor_response_time_seconds": SetWindowValue(window, value * 1000, ratio: false); break;
            }
        }

        public UptimeKumaMonitorMetric ToMetric() => new(MonitorId, Name, Type, Url, Hostname, Port, Status, ResponseTimeMilliseconds, UptimeRatio1Day, UptimeRatio30Days, UptimeRatio365Days, AverageResponseTimeMilliseconds1Day, AverageResponseTimeMilliseconds30Days, AverageResponseTimeMilliseconds365Days, CertificateIsValid, CertificateDaysRemaining);

        private void SetWindowValue(string? window, double value, bool ratio)
        {
            if (ratio)
            {
                switch (window)
                {
                    case "1d": UptimeRatio1Day = value; break;
                    case "30d": UptimeRatio30Days = value; break;
                    case "365d": UptimeRatio365Days = value; break;
                }
            }
            else
            {
                switch (window)
                {
                    case "1d": AverageResponseTimeMilliseconds1Day = value; break;
                    case "30d": AverageResponseTimeMilliseconds30Days = value; break;
                    case "365d": AverageResponseTimeMilliseconds365Days = value; break;
                }
            }
        }

        private static string? NullIfPrometheusNull(string? value) => string.IsNullOrWhiteSpace(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : value;
    }
}
