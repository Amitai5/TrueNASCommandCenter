using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.TrueNas;

public sealed record TrueNasAppDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = "STOPPED";

    [JsonPropertyName("upgrade_available")]
    public bool UpgradeAvailable { get; init; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; init; }

    [JsonPropertyName("latest_app_version")]
    public string? LatestAppVersion { get; init; }

    [JsonPropertyName("image_updates_available")]
    public bool ImageUpdatesAvailable { get; init; }

    [JsonPropertyName("custom_app")]
    public bool CustomApp { get; init; }

    [JsonPropertyName("human_version")]
    public string? HumanVersion { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("action_required")]
    public bool ActionRequired { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; init; }

    [JsonPropertyName("active_workloads")]
    public JsonElement ActiveWorkloads { get; init; }

    [JsonPropertyName("portals")]
    public JsonElement Portals { get; init; }
}

public sealed record TrueNasContainerLogRequest(string AppId, string ContainerId, int TailLines = 500);

public sealed record TrueNasLogEntry(DateTimeOffset Timestamp, string ContainerId, string Message, string Stream = "stdout");

/// <summary>Represents one application resource sample from the TrueNAS app statistics event.</summary>
public sealed record TrueNasAppStatsDto
{
    [JsonPropertyName("app_name")]
    public string AppName { get; init; } = string.Empty;

    [JsonPropertyName("cpu_usage")]
    public int CpuUsage { get; init; }

    [JsonPropertyName("memory")]
    public long Memory { get; init; }

    [JsonPropertyName("networks")]
    public IReadOnlyList<TrueNasAppNetworkStatsDto> Networks { get; init; } = [];

    [JsonPropertyName("blkio")]
    public TrueNasAppBlockIoStatsDto BlockIo { get; init; } = new();
}

/// <summary>Represents per-interface application network throughput reported by TrueNAS.</summary>
public sealed record TrueNasAppNetworkStatsDto
{
    [JsonPropertyName("interface_name")]
    public string InterfaceName { get; init; } = string.Empty;

    [JsonPropertyName("rx_bytes")]
    public long ReceiveBytes { get; init; }

    [JsonPropertyName("tx_bytes")]
    public long TransmitBytes { get; init; }
}

/// <summary>Represents application block-I/O counters reported by TrueNAS.</summary>
public sealed record TrueNasAppBlockIoStatsDto
{
    [JsonPropertyName("read")]
    public long ReadBytes { get; init; }

    [JsonPropertyName("write")]
    public long WriteBytes { get; init; }
}

/// <summary>Represents the subset of TrueNAS storage-pool fields used by the dashboard.</summary>
public sealed record TrueNasPoolDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "UNKNOWN";

    [JsonPropertyName("healthy")]
    public bool Healthy { get; init; }

    [JsonPropertyName("warning")]
    public bool Warning { get; init; }

    [JsonPropertyName("status_detail")]
    public string? StatusDetail { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("allocated")]
    public long? Allocated { get; init; }

    [JsonPropertyName("free")]
    public long? Free { get; init; }

    [JsonPropertyName("fragmentation")]
    public string? Fragmentation { get; init; }

    [JsonPropertyName("scan")]
    public TrueNasPoolScanDto? Scan { get; init; }
}

/// <summary>Represents the current or most recent scrub or resilver reported for a pool.</summary>
public sealed record TrueNasPoolScanDto
{
    [JsonPropertyName("function")]
    public string Function { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("start_time")]
    public JsonElement StartTime { get; init; }

    [JsonPropertyName("end_time")]
    public JsonElement EndTime { get; init; }

    [JsonPropertyName("percentage")]
    public double? Percentage { get; init; }

    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    [JsonPropertyName("total_secs_left")]
    public long? TotalSecondsLeft { get; init; }
}

/// <summary>Represents one job visible to the authenticated TrueNAS API session.</summary>
public sealed record TrueNasJobDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("progress")]
    public TrueNasJobProgressDto? Progress { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "WAITING";

    [JsonPropertyName("time_started")]
    public JsonElement TimeStarted { get; init; }

    [JsonPropertyName("time_finished")]
    public JsonElement TimeFinished { get; init; }
}

/// <summary>Contains current progress for a TrueNAS job.</summary>
public sealed record TrueNasJobProgressDto
{
    [JsonPropertyName("percent")]
    public double? Percent { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record TrueNasMailMessage(string Subject, string Text, IReadOnlyList<string> Recipients);

public sealed record TrueNasUpgradeSummaryDto
{
    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; init; }

    [JsonPropertyName("latest_human_version")]
    public string? LatestHumanVersion { get; init; }

    [JsonPropertyName("upgrade_version")]
    public string? UpgradeVersion { get; init; }

    [JsonPropertyName("upgrade_human_version")]
    public string? UpgradeHumanVersion { get; init; }

    [JsonPropertyName("available_versions_for_upgrade")]
    public IReadOnlyList<TrueNasVersionInfoDto> AvailableVersions { get; init; } = [];

    [JsonPropertyName("changelog")]
    public string? Changelog { get; init; }
}

public sealed record TrueNasVersionInfoDto
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("human_version")]
    public string HumanVersion { get; init; } = string.Empty;
}

public sealed record TrueNasAuthResponseDto
{
    [JsonPropertyName("response_type")]
    public string ResponseType { get; init; } = string.Empty;

    [JsonPropertyName("user_info")]
    public JsonElement? UserInfo { get; init; }
}

public sealed record TrueNasJobResult(long JobId, string State, JsonElement? Result = null);
