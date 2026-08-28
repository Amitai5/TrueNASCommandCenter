namespace TrueNasAppManager.Domain;

/// <summary>Contains display-safe identity, hardware, load, and uptime information for a TrueNAS host.</summary>
public sealed record TrueNasHostInformation(
    string Hostname,
    string Version,
    string CpuModel,
    long PhysicalMemoryBytes,
    int CoreCount,
    int PhysicalCoreCount,
    double? LoadAverageOneMinute,
    double? LoadAverageFiveMinutes,
    double? LoadAverageFifteenMinutes,
    string Uptime,
    DateTimeOffset BootTimeUtc,
    string TimeZoneId,
    string? SystemManufacturer,
    string? SystemProduct,
    bool HasEccMemory);

/// <summary>Represents the optional TrueNAS host-information capability.</summary>
public sealed record TrueNasHostOverview(TrueNasHostInformation? Information, bool RequiresReadOnlyAdmin = false, string? Error = null)
{
    public bool IsAvailable => Information is not null && !RequiresReadOnlyAdmin && string.IsNullOrWhiteSpace(Error);
}

/// <summary>Contains read-only TrueNAS operating-system update information.</summary>
public sealed record TrueNasUpdateInformation(
    string? Train,
    string? Profile,
    bool? MatchesProfile,
    string? AvailableVersion,
    string? ReleaseNotes,
    Uri? ReleaseNotesUri,
    double? DownloadPercent,
    string? DownloadDescription,
    string? DownloadVersion,
    string? CheckError)
{
    public bool IsUpdateAvailable => !string.IsNullOrWhiteSpace(AvailableVersion);
    public bool IsDownloading => DownloadPercent is not null;
}

/// <summary>Represents the optional TrueNAS operating-system update-status capability.</summary>
public sealed record TrueNasUpdateOverview(TrueNasUpdateInformation? Information, bool RequiresSystemUpdateRead = false, string? Error = null)
{
    public bool IsAvailable => Information is not null && !RequiresSystemUpdateRead && string.IsNullOrWhiteSpace(Error);
}

/// <summary>Represents one display-safe TrueNAS system alert.</summary>
public sealed record TrueNasSystemAlert(
    string Id,
    string Source,
    string ClassName,
    string Node,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastOccurrenceUtc,
    bool IsDismissed,
    string Text,
    string Severity,
    bool IsOneShot);

/// <summary>Represents the optional TrueNAS alert-list capability.</summary>
public sealed record TrueNasAlertOverview(IReadOnlyList<TrueNasSystemAlert> Alerts, bool RequiresAlertListRead = false, string? Error = null)
{
    public bool IsAvailable => !RequiresAlertListRead && string.IsNullOrWhiteSpace(Error);
    public int ActiveCount => Alerts.Count(alert => !alert.IsDismissed);
    public int CriticalCount => Alerts.Count(alert => !alert.IsDismissed && alert.Severity is "EMERGENCY" or "ALERT" or "CRITICAL");
}

/// <summary>Contains the complete read-only TrueNAS host overview.</summary>
public sealed record TrueNasSystemOverview(TrueNasHostOverview Host, TrueNasUpdateOverview Update, TrueNasAlertOverview Alerts, StoragePoolOverview Storage);
