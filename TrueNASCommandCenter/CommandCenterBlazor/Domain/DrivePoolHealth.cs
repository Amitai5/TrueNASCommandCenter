namespace TrueNasCommandCenter.Domain;

/// <summary>Describes whether one independently authorized TrueNAS drive-health source is available.</summary>
public sealed record DriveHealthSourceState(string Name, string RequiredRole, bool IsAvailable, string? Error = null);

/// <summary>Contains display-safe health and error counts for one pool vdev.</summary>
public sealed record PoolVdevHealth(string PoolName, string Group, string Name, string Type, string Status, int Depth, string? DiskName, long ReadErrors, long WriteErrors, long ChecksumErrors)
{
    public long TotalErrors => ReadErrors + WriteErrors + ChecksumErrors;
}

/// <summary>Contains pool health, topology, error counters, and scrub or resilver progress.</summary>
public sealed record PoolHealthDetail(string Name, string Status, bool IsHealthy, bool HasWarning, string? StatusDetail, long? SizeBytes, long? AllocatedBytes, string ScanFunction, string ScanState, double? ScanPercentage, int ScanErrors, DateTimeOffset? ScanStartedUtc, DateTimeOffset? ScanFinishedUtc, long? ScanSecondsRemaining, IReadOnlyList<PoolVdevHealth> Vdevs)
{
    public long TotalVdevErrors => Vdevs.Sum(vdev => vdev.TotalErrors);
    public bool IsScanRunning => ScanState is "SCANNING" or "RUNNING";
}

/// <summary>Contains display-safe identity, temperature, SMART configuration, and membership for one drive.</summary>
public sealed record DriveHealthDetail(string Name, string? Model, string? Serial, long? CapacityBytes, string? Type, string? Bus, int? RotationRate, bool IsSmartEnabled, double? TemperatureCelsius, double? CriticalTemperatureCelsius, string TemperatureState, string? PoolName, string? VdevGroup, string? VdevName, long ReadErrors, long WriteErrors, long ChecksumErrors, int WarningCount)
{
    public long TotalErrors => ReadErrors + WriteErrors + ChecksumErrors;
}

/// <summary>Represents one active TrueNAS warning associated with storage or SMART health.</summary>
public sealed record DriveHealthWarning(string Severity, string Title, string Detail, string? DiskName = null, string? PoolName = null);

/// <summary>Contains the complete read-only drive and pool health snapshot.</summary>
public sealed record DrivePoolHealthOverview(IReadOnlyList<PoolHealthDetail> Pools, IReadOnlyList<DriveHealthDetail> Drives, IReadOnlyList<DriveHealthWarning> Warnings, IReadOnlyList<DriveHealthSourceState> Sources, DateTimeOffset ObservedAtUtc)
{
    public int HealthyPoolCount => Pools.Count(pool => pool.IsHealthy && !pool.HasWarning && pool.TotalVdevErrors == 0);
    public int WarningDriveCount => Drives.Count(drive => drive.WarningCount > 0 || drive.TotalErrors > 0 || drive.TemperatureState is "warning" or "danger" || !drive.IsSmartEnabled);
    public int ActiveScanCount => Pools.Count(pool => pool.IsScanRunning);
}
