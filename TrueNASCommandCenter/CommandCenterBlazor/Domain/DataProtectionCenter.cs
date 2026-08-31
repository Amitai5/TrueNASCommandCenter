namespace TrueNasCommandCenter.Domain;

/// <summary>Identifies the TrueNAS protection mechanism represented by a task.</summary>
public enum DataProtectionTaskKind
{
    Snapshot,
    Replication,
    CloudSync
}

/// <summary>Describes whether one independently authorized TrueNAS protection source is available.</summary>
public sealed record DataProtectionSourceState(string Name, string RequiredRole, bool IsAvailable, string? Error = null);

/// <summary>Describes snapshot and protection-task coverage for one dataset.</summary>
public sealed record DatasetProtectionStatus(string Name, string Type, int Depth, bool IsLocked, bool IsSystemDataset, bool IsWarningEligible, int SnapshotCount, DateTimeOffset? NewestSnapshotUtc, bool HasSnapshotTask, bool HasReplicationTask, bool HasCloudSyncTask)
{
    public bool IsProtected => HasSnapshotTask || HasReplicationTask || HasCloudSyncTask;
    public bool IsUnprotected => IsWarningEligible && !IsProtected;
}

/// <summary>Contains display-safe state for one snapshot, replication, or cloud-sync task.</summary>
public sealed record DataProtectionTaskStatus(DataProtectionTaskKind Kind, int Id, string Name, string Source, string? Destination, bool IsEnabled, string State, double? ProgressPercent, DateTimeOffset? LastSuccessUtc, DateTimeOffset? NextRunUtc, string Schedule, string? Error);

/// <summary>Represents one actionable data-protection coverage or task warning.</summary>
public sealed record DataProtectionWarning(string Severity, string Title, string Detail, string? Dataset = null);

/// <summary>Contains the complete read-only data-protection center snapshot.</summary>
public sealed record DataProtectionCenterOverview(IReadOnlyList<DatasetProtectionStatus> Datasets, IReadOnlyList<DataProtectionTaskStatus> Tasks, IReadOnlyList<DataProtectionWarning> Warnings, IReadOnlyList<DataProtectionSourceState> Sources, DateTimeOffset ObservedAtUtc, string TimeZoneId)
{
    public int ProtectedDatasetCount => Datasets.Count(dataset => dataset.IsWarningEligible && dataset.IsProtected);
    public int EligibleDatasetCount => Datasets.Count(dataset => dataset.IsWarningEligible);
    public int UnprotectedDatasetCount => Datasets.Count(dataset => dataset.IsUnprotected);
    public int FailedTaskCount => Tasks.Count(task => task.IsEnabled && task.State is "FAILED" or "ERROR" or "ABORTED");
}
