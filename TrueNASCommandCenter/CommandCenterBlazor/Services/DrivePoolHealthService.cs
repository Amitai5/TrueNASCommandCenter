using System.Text.Json;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

/// <summary>Loads read-only TrueNAS pool topology, drive identity, temperature, and SMART-related warning data.</summary>
public interface IDrivePoolHealthService
{
    /// <summary>Loads every independently authorized source for drive and pool health.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current drive and pool health snapshot with explicit source availability.</returns>
    Task<DrivePoolHealthOverview> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Aggregates TrueNAS drive and pool state while preserving useful partial results when optional roles are missing.</summary>
public sealed class DrivePoolHealthService(ITrueNasDriveHealthClient trueNasClient, TimeProvider timeProvider, ILogger<DrivePoolHealthService> logger) : IDrivePoolHealthService
{
    /// <inheritdoc />
    public async Task<DrivePoolHealthOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        var poolsTask = LoadAsync("Pools and vdevs", "POOL_READ", trueNasClient.QueryPoolsAsync, cancellationToken);
        var disksTask = LoadAsync("Drive identity", "DISK_READ", trueNasClient.QueryDisksAsync, cancellationToken);
        var alertsTask = LoadAsync("SMART warnings", "ALERT_LIST_READ", trueNasClient.ListAlertsAsync, cancellationToken);

        await Task.WhenAll(poolsTask, disksTask, alertsTask);
        var pools = await poolsTask;
        var disks = await disksTask;
        var alerts = await alertsTask;
        var temperatures = await LoadTemperaturesAsync(disks.Items, cancellationToken);

        var mappedPools = MapPools(pools.Items);
        var relevantAlerts = alerts.Items.Where(IsDriveOrPoolAlert).ToList();
        var mappedWarnings = MapWarnings(relevantAlerts, disks.Items, mappedPools);
        var mappedDrives = MapDrives(disks.Items, mappedPools, temperatures.Items, relevantAlerts);
        var sources = new[] { pools.Source, disks.Source, temperatures.Source, alerts.Source };
        return new DrivePoolHealthOverview(mappedPools, mappedDrives, mappedWarnings, sources, timeProvider.GetUtcNow());
    }

    private async Task<SourceResult<T>> LoadAsync<T>(string name, string role, Func<CancellationToken, Task<IReadOnlyList<T>>> loader, CancellationToken cancellationToken)
    {
        try
        {
            return new SourceResult<T>(await loader(cancellationToken), new DriveHealthSourceState(name, role, true));
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("Drive health source {SourceName} is unavailable because the API account lacks {RequiredRole}", name, role);
            return new SourceResult<T>([], new DriveHealthSourceState(name, role, false, $"Add {role} to the TrueNAS API account."));
        }
        catch (InvalidOperationException exception) when (IsMissingCredentials(exception))
        {
            logger.LogDebug("Drive health source {SourceName} is unavailable until the TrueNAS connection is configured", name);
            return new SourceResult<T>([], new DriveHealthSourceState(name, role, false, "Connect TrueNAS in Settings."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Drive health source {SourceName} could not be loaded", name);
            return new SourceResult<T>([], new DriveHealthSourceState(name, role, false, "Temporarily unavailable."));
        }
    }

    private static bool IsMissingCredentials(InvalidOperationException exception) => exception.Message.Contains("username and API key are required", StringComparison.OrdinalIgnoreCase);

    private async Task<TemperatureResult> LoadTemperaturesAsync(IReadOnlyList<TrueNasDiskDto> disks, CancellationToken cancellationToken)
    {
        if (disks.Count == 0)
        {
            return new TemperatureResult(new Dictionary<string, JsonElement>(), new DriveHealthSourceState("Temperatures", "REPORTING_READ", false, "Drive identity must be available first."));
        }

        try
        {
            var names = disks.Select(disk => FirstNotBlank(disk.Name, disk.DeviceName)).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var temperatures = await trueNasClient.GetDiskTemperaturesAsync(names, cancellationToken);
            return new TemperatureResult(temperatures, new DriveHealthSourceState("Temperatures", "REPORTING_READ", true));
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("Drive temperatures are unavailable because the API account lacks REPORTING_READ");
            return new TemperatureResult(new Dictionary<string, JsonElement>(), new DriveHealthSourceState("Temperatures", "REPORTING_READ", false, "Add REPORTING_READ to the TrueNAS API account."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Drive temperatures could not be loaded");
            return new TemperatureResult(new Dictionary<string, JsonElement>(), new DriveHealthSourceState("Temperatures", "REPORTING_READ", false, "Temporarily unavailable."));
        }
    }

    private static IReadOnlyList<PoolHealthDetail> MapPools(IReadOnlyList<TrueNasPoolDto> pools) => pools
        .OrderBy(pool => pool.Name, StringComparer.OrdinalIgnoreCase)
        .Select(pool =>
        {
            var vdevs = new List<PoolVdevHealth>();
            if (pool.Topology is { } topology)
            {
                AddVdevs(vdevs, pool.Name, "Data", topology.Data);
                AddVdevs(vdevs, pool.Name, "Log", topology.Log);
                AddVdevs(vdevs, pool.Name, "Cache", topology.Cache);
                AddVdevs(vdevs, pool.Name, "Spare", topology.Spare);
                AddVdevs(vdevs, pool.Name, "Special", topology.Special);
                AddVdevs(vdevs, pool.Name, "Dedup", topology.Dedup);
            }

            return new PoolHealthDetail(
                pool.Name,
                NormalizeState(pool.Status, "UNKNOWN"),
                pool.Healthy,
                pool.Warning,
                pool.StatusDetail,
                pool.Size,
                pool.Allocated,
                NormalizeState(pool.Scan?.Function, "NONE"),
                NormalizeState(pool.Scan?.State, "IDLE"),
                pool.Scan?.Percentage is null ? null : Math.Clamp(pool.Scan.Percentage.Value, 0, 100),
                pool.Scan?.Errors ?? 0,
                pool.Scan is null ? null : TrueNasJsonValueReader.FindDate(pool.Scan.StartTime),
                pool.Scan is null ? null : TrueNasJsonValueReader.FindDate(pool.Scan.EndTime),
                pool.Scan?.TotalSecondsLeft,
                vdevs);
        })
        .ToList();

    private static IReadOnlyList<DriveHealthDetail> MapDrives(IReadOnlyList<TrueNasDiskDto> disks, IReadOnlyList<PoolHealthDetail> pools, IReadOnlyDictionary<string, JsonElement> temperatures, IReadOnlyList<TrueNasAlertDto> alerts)
    {
        return disks
            .OrderBy(disk => disk.Name, StringComparer.OrdinalIgnoreCase)
            .Select(disk =>
            {
                var diskName = FirstNotBlank(disk.Name, disk.DeviceName);
                var membership = FindMembership(pools, diskName, disk.DeviceName);
                var temperaturePayload = FindTemperature(temperatures, diskName, disk.DeviceName);
                var temperature = TemperatureValue(temperaturePayload);
                var critical = CriticalTemperatureValue(temperaturePayload);
                var matchingAlerts = alerts.Count(alert => AlertMatchesDisk(alert, disk));
                return new DriveHealthDetail(
                    diskName,
                    NullIfWhiteSpace(disk.Model),
                    NullIfWhiteSpace(disk.Serial),
                    disk.Size,
                    NullIfWhiteSpace(disk.Type),
                    NullIfWhiteSpace(disk.Bus),
                    disk.RotationRate,
                    disk.SmartEnabled,
                    temperature,
                    critical,
                    TemperatureState(temperature, critical),
                    membership?.PoolName ?? NullIfWhiteSpace(disk.Pool),
                    membership?.Group,
                    membership?.Name,
                    membership?.ReadErrors ?? 0,
                    membership?.WriteErrors ?? 0,
                    membership?.ChecksumErrors ?? 0,
                    matchingAlerts);
            })
            .ToList();
    }

    private static IReadOnlyList<DriveHealthWarning> MapWarnings(IReadOnlyList<TrueNasAlertDto> alerts, IReadOnlyList<TrueNasDiskDto> disks, IReadOnlyList<PoolHealthDetail> pools) => alerts
        .Where(alert => !alert.IsDismissed)
        .OrderBy(alert => SeverityPriority(alert.Level))
        .ThenByDescending(alert => alert.LastOccurrence)
        .Select(alert =>
        {
            var disk = disks.FirstOrDefault(item => AlertMatchesDisk(alert, item));
            var pool = pools.FirstOrDefault(item => CombinedAlertText(alert).Contains(item.Name, StringComparison.OrdinalIgnoreCase));
            return new DriveHealthWarning(
                NormalizeSeverity(alert.Level),
                string.IsNullOrWhiteSpace(alert.ClassName) ? "Storage warning" : Humanize(alert.ClassName),
                NormalizeText(FirstNotBlank(alert.Text, alert.ClassName, "TrueNAS reported a storage warning.")),
                disk is null ? null : FirstNotBlank(disk.Name, disk.DeviceName),
                pool?.Name);
        })
        .ToList();

    private static void AddVdevs(List<PoolVdevHealth> target, string poolName, string group, IReadOnlyList<TrueNasVdevDto> vdevs, int depth = 0)
    {
        foreach (var vdev in vdevs)
        {
            var diskName = NormalizeDeviceName(FirstNotBlank(vdev.Disk, vdev.Device, vdev.Path, vdev.Name));
            target.Add(new PoolVdevHealth(
                poolName,
                group,
                FirstNotBlank(vdev.Name, diskName, "Unknown vdev"),
                FirstNotBlank(vdev.Type, "Unknown"),
                NormalizeState(vdev.Status, "UNKNOWN"),
                depth,
                vdev.Children.Count == 0 ? diskName : null,
                vdev.Stats?.ReadErrors ?? 0,
                vdev.Stats?.WriteErrors ?? 0,
                vdev.Stats?.ChecksumErrors ?? 0));
            AddVdevs(target, poolName, group, vdev.Children, depth + 1);
        }
    }

    private static PoolVdevHealth? FindMembership(IReadOnlyList<PoolHealthDetail> pools, params string[] diskNames)
    {
        var normalized = diskNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(NormalizeDeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pools.SelectMany(pool => pool.Vdevs).FirstOrDefault(vdev => vdev.DiskName is not null && normalized.Contains(NormalizeDeviceName(vdev.DiskName)));
    }

    private static JsonElement FindTemperature(IReadOnlyDictionary<string, JsonElement> temperatures, params string[] diskNames)
    {
        foreach (var diskName in diskNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var normalized = NormalizeDeviceName(diskName);
            var match = temperatures.FirstOrDefault(pair => string.Equals(NormalizeDeviceName(pair.Key), normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                return match.Value;
            }
        }

        return default;
    }

    private static double? TemperatureValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(item => TrueNasJsonValueReader.FindDouble(item)).FirstOrDefault(value => value is not null);
        }

        return TrueNasJsonValueReader.FindDouble(element, "temperature", "current", "value");
    }

    private static double? CriticalTemperatureValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Skip(1).Select(item => TrueNasJsonValueReader.FindDouble(item)).FirstOrDefault(value => value is not null);
        }

        return TrueNasJsonValueReader.FindDouble(element, "critical", "critical_temperature", "threshold");
    }

    private static string TemperatureState(double? temperature, double? critical)
    {
        if (temperature is null)
        {
            return "unknown";
        }

        if (temperature >= (critical ?? 65))
        {
            return "danger";
        }

        if (temperature >= Math.Min((critical ?? 60) - 5, 55))
        {
            return "warning";
        }

        return "success";
    }

    private static bool IsDriveOrPoolAlert(TrueNasAlertDto alert)
    {
        if (alert.IsDismissed)
        {
            return false;
        }

        var text = CombinedAlertText(alert);
        return new[] { "smart", "disk", "drive", "ata", "nvme", "sector", "temperature", "pool", "zfs", "scrub", "resilver" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AlertMatchesDisk(TrueNasAlertDto alert, TrueNasDiskDto disk)
    {
        var text = CombinedAlertText(alert);
        return new[] { disk.Name, disk.DeviceName, disk.Serial, disk.Identifier }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => text.Contains(value!, StringComparison.OrdinalIgnoreCase));
    }

    private static string CombinedAlertText(TrueNasAlertDto alert) => $"{alert.ClassName} {alert.Text} {alert.Source} {alert.Node}";

    private static string NormalizeDeviceName(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string NormalizeState(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('_', ' ').ToUpperInvariant();

    private static string NormalizeSeverity(string? value) => string.IsNullOrWhiteSpace(value) ? "WARNING" : value.Trim().ToUpperInvariant();

    private static int SeverityPriority(string? severity) => NormalizeSeverity(severity) switch
    {
        "EMERGENCY" => 0,
        "ALERT" => 1,
        "CRITICAL" => 2,
        "ERROR" => 3,
        "WARNING" => 4,
        _ => 5
    };

    private static string Humanize(string value)
    {
        var characters = value.Replace('_', ' ').ToCharArray();
        for (var index = 1; index < characters.Length; index++)
        {
            if (char.IsUpper(characters[index]) && char.IsLower(characters[index - 1]))
            {
                characters[index] = char.ToLowerInvariant(characters[index]);
            }
        }

        return new string(characters);
    }

    private static string NormalizeText(string value)
    {
        const int maximumLength = 2_000;
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : $"{normalized[..(maximumLength - 1)]}…";
    }

    private static string FirstNotBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsPermissionFailure(TrueNasClientException exception) => exception.Code is "-32001" or "EACCES" or "EPERM" ||
        exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("role", StringComparison.OrdinalIgnoreCase);

    private sealed record SourceResult<T>(IReadOnlyList<T> Items, DriveHealthSourceState Source);
    private sealed record TemperatureResult(IReadOnlyDictionary<string, JsonElement> Items, DriveHealthSourceState Source);
}
