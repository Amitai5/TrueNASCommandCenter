using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Identifies one reporting graph and its available host-specific identifiers.</summary>
public sealed record TrueNasPerformanceGraphDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("identifiers")]
    public IReadOnlyList<string>? Identifiers { get; init; }
}

/// <summary>Requests one reporting graph and optional host-specific identifier.</summary>
public sealed record TrueNasPerformanceGraphRequestDto(string Name, string? Identifier = null);

/// <summary>Contains one raw historical reporting graph returned by TrueNAS.</summary>
public sealed record TrueNasPerformanceDataDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("data")]
    public IReadOnlyList<JsonElement> Data { get; init; } = [];

    [JsonPropertyName("legend")]
    public IReadOnlyList<string> Legend { get; init; } = [];

    [JsonPropertyName("start")]
    public long Start { get; init; }

    [JsonPropertyName("end")]
    public long End { get; init; }
}

/// <summary>Contains one realtime reporting event returned by TrueNAS.</summary>
public sealed record TrueNasRealtimePerformanceDto
{
    [JsonPropertyName("cpu")]
    public JsonElement Cpu { get; init; }

    [JsonPropertyName("memory")]
    public JsonElement Memory { get; init; }

    [JsonPropertyName("interfaces")]
    public JsonElement Interfaces { get; init; }

    [JsonPropertyName("disks")]
    public JsonElement Disks { get; init; }

    [JsonPropertyName("zfs")]
    public JsonElement Zfs { get; init; }

    [JsonPropertyName("pools")]
    public JsonElement Pools { get; init; }

    [JsonPropertyName("load")]
    public JsonElement Load { get; init; }
}
