using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueNasAppManager.Integrations.TrueNas;

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
