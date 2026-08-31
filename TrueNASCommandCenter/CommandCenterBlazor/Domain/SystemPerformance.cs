namespace TrueNasCommandCenter.Domain;

/// <summary>Defines the historical window requested for TrueNAS performance charts.</summary>
public enum SystemPerformanceRange
{
    OneHour,
    TwentyFourHours,
    SevenDays,
    ThirtyDays
}

/// <summary>Defines how a performance chart value is formatted.</summary>
public enum SystemPerformanceUnit
{
    Percent,
    Bytes,
    BytesPerSecond,
    Load,
    Celsius
}

/// <summary>Represents one timestamped performance value.</summary>
public sealed record SystemPerformancePoint(DateTimeOffset TimestampUtc, double Value);

/// <summary>Represents one named line in a performance chart.</summary>
public sealed record SystemPerformanceSeries(string Label, IReadOnlyList<SystemPerformancePoint> Points);

/// <summary>Contains the display-ready series for one performance metric.</summary>
public sealed record SystemPerformanceChart(string Key, string Title, SystemPerformanceUnit Unit, IReadOnlyList<SystemPerformanceSeries> Series);

/// <summary>Contains historical TrueNAS performance data for one selected range.</summary>
public sealed record SystemPerformanceHistory(
    SystemPerformanceRange Range,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<SystemPerformanceChart> Charts,
    bool RequiresReportingRead = false,
    string? Error = null);

/// <summary>Represents current read and write activity for one storage pool.</summary>
public sealed record LivePoolActivity(string Name, double ReadBytesPerSecond, double WriteBytesPerSecond, double? BusyPercent);

/// <summary>Contains the latest realtime performance sample reported by TrueNAS.</summary>
public sealed record LiveSystemPerformance(
    DateTimeOffset ObservedAtUtc,
    double? CpuUsagePercent,
    double? CpuTemperatureCelsius,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? MemoryUsedPercent,
    double? LoadOneMinute,
    double NetworkReceiveBytesPerSecond,
    double NetworkSendBytesPerSecond,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    long? ArcSizeBytes,
    double? ArcHitPercent,
    IReadOnlyList<LivePoolActivity> Pools);
