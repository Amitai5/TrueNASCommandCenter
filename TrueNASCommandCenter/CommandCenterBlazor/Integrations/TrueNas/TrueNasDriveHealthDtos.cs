using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Represents display-safe identity and SMART configuration for one TrueNAS disk.</summary>
public sealed record TrueNasDiskDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("devname")]
    public string DeviceName { get; init; } = string.Empty;

    [JsonPropertyName("identifier")]
    public string Identifier { get; init; } = string.Empty;

    [JsonPropertyName("serial")]
    public string? Serial { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("bus")]
    public string? Bus { get; init; }

    [JsonPropertyName("rotationrate")]
    public int? RotationRate { get; init; }

    [JsonPropertyName("pool")]
    public string? Pool { get; init; }

    [JsonPropertyName("togglesmart")]
    public bool SmartEnabled { get; init; }
}

/// <summary>Represents the physical vdev topology returned with a storage pool.</summary>
public sealed record TrueNasPoolTopologyDto
{
    [JsonPropertyName("data")]
    public IReadOnlyList<TrueNasVdevDto> Data { get; init; } = [];

    [JsonPropertyName("log")]
    public IReadOnlyList<TrueNasVdevDto> Log { get; init; } = [];

    [JsonPropertyName("cache")]
    public IReadOnlyList<TrueNasVdevDto> Cache { get; init; } = [];

    [JsonPropertyName("spare")]
    public IReadOnlyList<TrueNasVdevDto> Spare { get; init; } = [];

    [JsonPropertyName("special")]
    public IReadOnlyList<TrueNasVdevDto> Special { get; init; } = [];

    [JsonPropertyName("dedup")]
    public IReadOnlyList<TrueNasVdevDto> Dedup { get; init; } = [];
}

/// <summary>Represents one pool vdev or leaf device.</summary>
public sealed record TrueNasVdevDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "UNKNOWN";

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("disk")]
    public string? Disk { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("stats")]
    public TrueNasVdevStatsDto? Stats { get; init; }

    [JsonPropertyName("children")]
    public IReadOnlyList<TrueNasVdevDto> Children { get; init; } = [];
}

/// <summary>Contains ZFS error counters for a pool vdev.</summary>
public sealed record TrueNasVdevStatsDto
{
    [JsonPropertyName("read_errors")]
    public long? ReadErrors { get; init; }

    [JsonPropertyName("write_errors")]
    public long? WriteErrors { get; init; }

    [JsonPropertyName("checksum_errors")]
    public long? ChecksumErrors { get; init; }
}
