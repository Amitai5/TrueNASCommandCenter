using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Represents the catalog fields returned by TrueNAS catalog APIs.</summary>
public sealed record TrueNasCatalogAppDto
{
    [JsonPropertyName("app_readme")]
    public string? AppReadme { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = [];

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("healthy")]
    public bool Healthy { get; init; }

    [JsonPropertyName("healthy_error")]
    public string? HealthyError { get; init; }

    [JsonPropertyName("home")]
    public string? Home { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; init; }

    [JsonPropertyName("latest_app_version")]
    public string? LatestAppVersion { get; init; }

    [JsonPropertyName("latest_human_version")]
    public string? LatestHumanVersion { get; init; }

    [JsonPropertyName("last_update")]
    public string? LastUpdate { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("recommended")]
    public bool Recommended { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("maintainers")]
    public IReadOnlyList<TrueNasCatalogMaintainerDto> Maintainers { get; init; } = [];

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("screenshots")]
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    [JsonPropertyName("sources")]
    public IReadOnlyList<string> Sources { get; init; } = [];

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<TrueNasCatalogCapabilityDto> Capabilities { get; init; } = [];

    [JsonPropertyName("run_as_context")]
    public IReadOnlyList<TrueNasCatalogRunAsContextDto> RunAsContext { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Represents a maintainer published in TrueNAS catalog metadata.</summary>
public sealed record TrueNasCatalogMaintainerDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Represents a Linux capability published in TrueNAS catalog metadata.</summary>
public sealed record TrueNasCatalogCapabilityDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}

/// <summary>Represents one catalog workload user and group context.</summary>
public sealed record TrueNasCatalogRunAsContextDto
{
    [JsonPropertyName("uid")]
    public int? UserId { get; init; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("gid")]
    public int? GroupId { get; init; }

    [JsonPropertyName("group_name")]
    public string? GroupName { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}
