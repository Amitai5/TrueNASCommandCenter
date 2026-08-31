using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Represents one TrueNAS dataset used by the protection coverage view.</summary>
public sealed record TrueNasDatasetDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "FILESYSTEM";

    [JsonPropertyName("locked")]
    public bool IsLocked { get; init; }
}

/// <summary>Represents one TrueNAS ZFS snapshot with display-safe creation metadata.</summary>
public sealed record TrueNasSnapshotDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("dataset")]
    public string Dataset { get; init; } = string.Empty;

    [JsonPropertyName("snapshot_name")]
    public string SnapshotName { get; init; } = string.Empty;

    [JsonPropertyName("properties")]
    public JsonElement Properties { get; init; }
}

/// <summary>Represents a five-field TrueNAS cron schedule.</summary>
public sealed record TrueNasCronScheduleDto
{
    [JsonPropertyName("minute")]
    public string Minute { get; init; } = "00";

    [JsonPropertyName("hour")]
    public string Hour { get; init; } = "*";

    [JsonPropertyName("dom")]
    public string DayOfMonth { get; init; } = "*";

    [JsonPropertyName("month")]
    public string Month { get; init; } = "*";

    [JsonPropertyName("dow")]
    public string DayOfWeek { get; init; } = "*";
}

/// <summary>Represents one periodic snapshot task.</summary>
public sealed record TrueNasSnapshotTaskDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("dataset")]
    public string Dataset { get; init; } = string.Empty;

    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("exclude")]
    public IReadOnlyList<string> Exclude { get; init; } = [];

    [JsonPropertyName("naming_schema")]
    public string NamingSchema { get; init; } = string.Empty;

    [JsonPropertyName("schedule")]
    public TrueNasCronScheduleDto? Schedule { get; init; }

    [JsonPropertyName("state")]
    public JsonElement State { get; init; }
}

/// <summary>Represents one configured TrueNAS replication task.</summary>
public sealed record TrueNasReplicationTaskDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = string.Empty;

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = string.Empty;

    [JsonPropertyName("source_datasets")]
    public IReadOnlyList<string> SourceDatasets { get; init; } = [];

    [JsonPropertyName("target_dataset")]
    public string? TargetDataset { get; init; }

    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("auto")]
    public bool Automatic { get; init; }

    [JsonPropertyName("schedule")]
    public TrueNasCronScheduleDto? Schedule { get; init; }

    [JsonPropertyName("periodic_snapshot_tasks")]
    public IReadOnlyList<int> PeriodicSnapshotTaskIds { get; init; } = [];

    [JsonPropertyName("state")]
    public JsonElement State { get; init; }

    [JsonPropertyName("job")]
    public JsonElement Job { get; init; }
}

/// <summary>Represents one configured TrueNAS cloud-sync task without credential details.</summary>
public sealed record TrueNasCloudSyncTaskDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = string.Empty;

    [JsonPropertyName("transfer_mode")]
    public string TransferMode { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("locked")]
    public bool IsLocked { get; init; }

    [JsonPropertyName("schedule")]
    public TrueNasCronScheduleDto? Schedule { get; init; }

    [JsonPropertyName("job")]
    public JsonElement Job { get; init; }
}
