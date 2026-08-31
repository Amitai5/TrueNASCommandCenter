namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Reads TrueNAS datasets, snapshots, and data-protection task state.</summary>
public interface ITrueNasDataProtectionClient
{
    /// <summary>Returns all datasets visible to the authenticated account as a flat list.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The visible TrueNAS datasets.</returns>
    Task<IReadOnlyList<TrueNasDatasetDto>> QueryDatasetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns all visible snapshots with their creation property.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The visible TrueNAS snapshots.</returns>
    Task<IReadOnlyList<TrueNasSnapshotDto>> QuerySnapshotsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns configured periodic snapshot tasks.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The configured periodic snapshot tasks.</returns>
    Task<IReadOnlyList<TrueNasSnapshotTaskDto>> QuerySnapshotTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns configured replication tasks.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The configured replication tasks.</returns>
    Task<IReadOnlyList<TrueNasReplicationTaskDto>> QueryReplicationTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns configured cloud-sync tasks without credential secrets.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The configured cloud-sync tasks.</returns>
    Task<IReadOnlyList<TrueNasCloudSyncTaskDto>> QueryCloudSyncTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns recent jobs used to supplement task last-run state.</summary>
    /// <param name="limit">The maximum number of recent jobs to return.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The recent jobs visible to the current API session.</returns>
    Task<IReadOnlyList<TrueNasJobDto>> ListProtectionJobsAsync(int limit = 500, CancellationToken cancellationToken = default);
}
