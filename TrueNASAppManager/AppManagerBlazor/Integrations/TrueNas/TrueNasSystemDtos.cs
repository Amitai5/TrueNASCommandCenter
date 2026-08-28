using System.Text.Json.Serialization;

namespace TrueNasAppManager.Integrations.TrueNas;

/// <summary>Represents the display-safe subset of TrueNAS host information.</summary>
public sealed record TrueNasSystemInfoDto
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    [JsonPropertyName("physmem")]
    public long PhysicalMemory { get; init; }

    [JsonPropertyName("model")]
    public string CpuModel { get; init; } = string.Empty;

    [JsonPropertyName("cores")]
    public int CoreCount { get; init; }

    [JsonPropertyName("physical_cores")]
    public int PhysicalCoreCount { get; init; }

    [JsonPropertyName("loadavg")]
    public IReadOnlyList<double> LoadAverage { get; init; } = [];

    [JsonPropertyName("uptime")]
    public string Uptime { get; init; } = string.Empty;

    [JsonPropertyName("uptime_seconds")]
    public double UptimeSeconds { get; init; }

    [JsonPropertyName("boottime")]
    public DateTimeOffset BootTime { get; init; }

    [JsonPropertyName("timezone")]
    public string TimeZoneId { get; init; } = string.Empty;

    [JsonPropertyName("system_manufacturer")]
    public string? SystemManufacturer { get; init; }

    [JsonPropertyName("system_product")]
    public string? SystemProduct { get; init; }

    [JsonPropertyName("ecc_memory")]
    public bool HasEccMemory { get; init; }
}

/// <summary>Represents one current TrueNAS alert.</summary>
public sealed record TrueNasAlertDto
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("klass")]
    public string ClassName { get; init; } = string.Empty;

    [JsonPropertyName("node")]
    public string Node { get; init; } = string.Empty;

    [JsonPropertyName("datetime")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("last_occurrence")]
    public DateTimeOffset LastOccurrence { get; init; }

    [JsonPropertyName("dismissed")]
    public bool IsDismissed { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "INFO";

    [JsonPropertyName("one_shot")]
    public bool IsOneShot { get; init; }
}

/// <summary>Represents the current TrueNAS operating-system update status.</summary>
public sealed record TrueNasUpdateStatusDto
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "ERROR";

    [JsonPropertyName("status")]
    public TrueNasUpdateStatusDetailsDto? Status { get; init; }

    [JsonPropertyName("error")]
    public TrueNasUpdateErrorDto? Error { get; init; }

    [JsonPropertyName("update_download_progress")]
    public TrueNasUpdateDownloadProgressDto? DownloadProgress { get; init; }
}

/// <summary>Contains the current and available TrueNAS operating-system versions.</summary>
public sealed record TrueNasUpdateStatusDetailsDto
{
    [JsonPropertyName("current_version")]
    public TrueNasCurrentVersionDto? CurrentVersion { get; init; }

    [JsonPropertyName("new_version")]
    public TrueNasAvailableVersionDto? NewVersion { get; init; }
}

/// <summary>Contains the configured update train and profile for the running TrueNAS version.</summary>
public sealed record TrueNasCurrentVersionDto
{
    [JsonPropertyName("train")]
    public string? Train { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("matches_profile")]
    public bool MatchesProfile { get; init; }
}

/// <summary>Contains the version and release information for an available TrueNAS update.</summary>
public sealed record TrueNasAvailableVersionDto
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("release_notes")]
    public string? ReleaseNotes { get; init; }

    [JsonPropertyName("release_notes_url")]
    public string? ReleaseNotesUrl { get; init; }
}

/// <summary>Describes an error returned by the TrueNAS update-status provider.</summary>
public sealed record TrueNasUpdateErrorDto
{
    [JsonPropertyName("errname")]
    public string ErrorName { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Describes a TrueNAS operating-system update download in progress.</summary>
public sealed record TrueNasUpdateDownloadProgressDto
{
    [JsonPropertyName("percent")]
    public double Percent { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}
