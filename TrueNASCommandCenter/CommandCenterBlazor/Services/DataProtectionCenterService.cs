using System.Text.Json;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Scheduling;

namespace TrueNasCommandCenter.Services;

/// <summary>Loads the read-only dataset, snapshot, replication, and cloud-sync protection view.</summary>
public interface IDataProtectionCenterService
{
    /// <summary>Loads every independently authorized source for the data-protection center.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current protection snapshot with explicit source availability.</returns>
    Task<DataProtectionCenterOverview> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Aggregates TrueNAS protection configuration and task state without allowing one missing role to hide other data.</summary>
public sealed class DataProtectionCenterService(ITrueNasDataProtectionClient trueNasClient, SettingsService settingsService, IScheduleService scheduleService, TimeProvider timeProvider, ILogger<DataProtectionCenterService> logger) : IDataProtectionCenterService
{
    /// <inheritdoc />
    public async Task<DataProtectionCenterOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        var datasetsTask = LoadAsync("Datasets", "DATASET_READ", trueNasClient.QueryDatasetsAsync, cancellationToken);
        var snapshotsTask = LoadAsync("Snapshots", "SNAPSHOT_READ", trueNasClient.QuerySnapshotsAsync, cancellationToken);
        var snapshotTasksTask = LoadAsync("Snapshot tasks", "SNAPSHOT_TASK_READ", trueNasClient.QuerySnapshotTasksAsync, cancellationToken);
        var replicationTasksTask = LoadAsync("Replication", "REPLICATION_TASK_READ", trueNasClient.QueryReplicationTasksAsync, cancellationToken);
        var cloudSyncTasksTask = LoadAsync("Cloud sync", "CLOUD_SYNC_READ", trueNasClient.QueryCloudSyncTasksAsync, cancellationToken);
        var jobsTask = LoadJobsAsync(cancellationToken);
        var settingsTask = LoadTimeZoneAsync(cancellationToken);

        await Task.WhenAll(datasetsTask, snapshotsTask, snapshotTasksTask, replicationTasksTask, cloudSyncTasksTask, jobsTask, settingsTask);

        var datasets = await datasetsTask;
        var snapshots = await snapshotsTask;
        var snapshotTasks = await snapshotTasksTask;
        var replicationTasks = await replicationTasksTask;
        var cloudSyncTasks = await cloudSyncTasksTask;
        var jobs = await jobsTask;
        var timeZoneId = await settingsTask;
        var observedAt = timeProvider.GetUtcNow();

        var mappedTasks = MapTasks(snapshotTasks.Items, replicationTasks.Items, cloudSyncTasks.Items, jobs, timeZoneId, observedAt);
        var mappedDatasets = MapDatasets(datasets.Items, snapshots.Items, snapshotTasks.Items, replicationTasks.Items, cloudSyncTasks.Items);
        var warnings = BuildWarnings(mappedDatasets, mappedTasks);
        var sources = new[] { datasets.Source, snapshots.Source, snapshotTasks.Source, replicationTasks.Source, cloudSyncTasks.Source };

        return new DataProtectionCenterOverview(mappedDatasets, mappedTasks, warnings, sources, observedAt, timeZoneId);
    }

    private async Task<SourceResult<T>> LoadAsync<T>(string name, string role, Func<CancellationToken, Task<IReadOnlyList<T>>> loader, CancellationToken cancellationToken)
    {
        try
        {
            return new SourceResult<T>(await loader(cancellationToken), new DataProtectionSourceState(name, role, true));
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("Data protection source {SourceName} is unavailable because the API account lacks {RequiredRole}", name, role);
            return new SourceResult<T>([], new DataProtectionSourceState(name, role, false, $"Add {role} to the TrueNAS API account."));
        }
        catch (InvalidOperationException exception) when (IsMissingCredentials(exception))
        {
            logger.LogDebug("Data protection source {SourceName} is unavailable until the TrueNAS connection is configured", name);
            return new SourceResult<T>([], new DataProtectionSourceState(name, role, false, "Connect TrueNAS in Settings."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Data protection source {SourceName} could not be loaded", name);
            return new SourceResult<T>([], new DataProtectionSourceState(name, role, false, "Temporarily unavailable."));
        }
    }

    private static bool IsMissingCredentials(InvalidOperationException exception) => exception.Message.Contains("username and API key are required", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<TrueNasJobDto>> LoadJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await trueNasClient.ListProtectionJobsAsync(cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Recent TrueNAS jobs were unavailable; embedded task state will be used");
            return [];
        }
    }

    private async Task<string> LoadTimeZoneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsService.GetRecordAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(settings.TimeZoneId) ? "Etc/UTC" : settings.TimeZoneId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "The configured display timezone was unavailable; UTC will be used");
            return "Etc/UTC";
        }
    }

    private IReadOnlyList<DatasetProtectionStatus> MapDatasets(IReadOnlyList<TrueNasDatasetDto> datasets, IReadOnlyList<TrueNasSnapshotDto> snapshots, IReadOnlyList<TrueNasSnapshotTaskDto> snapshotTasks, IReadOnlyList<TrueNasReplicationTaskDto> replicationTasks, IReadOnlyList<TrueNasCloudSyncTaskDto> cloudSyncTasks)
    {
        var snapshotsByDataset = snapshots
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Dataset))
            .GroupBy(snapshot => snapshot.Dataset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return datasets
            .Where(dataset => !string.IsNullOrWhiteSpace(dataset.Name))
            .OrderBy(dataset => dataset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(dataset =>
            {
                snapshotsByDataset.TryGetValue(dataset.Name, out var datasetSnapshots);
                var newestSnapshot = datasetSnapshots?
                    .Select(snapshot => TrueNasJsonValueReader.FindDate(snapshot.Properties, "creation"))
                    .Where(value => value is not null)
                    .Max();
                var depth = dataset.Name.Count(character => character == '/');
                var isSystem = IsSystemDataset(dataset.Name);
                return new DatasetProtectionStatus(
                    dataset.Name,
                    dataset.Type,
                    depth,
                    dataset.IsLocked,
                    isSystem,
                    depth > 0 && !isSystem,
                    datasetSnapshots?.Count ?? 0,
                    newestSnapshot,
                    snapshotTasks.Any(task => task.Enabled && DatasetMatches(dataset.Name, task.Dataset, task.Recursive, task.Exclude)),
                    replicationTasks.Any(task => task.Enabled && IsPushDirection(task.Direction) && task.SourceDatasets.Any(source => DatasetMatches(dataset.Name, source, task.Recursive, []))),
                    cloudSyncTasks.Any(task => task.Enabled && IsPushDirection(task.Direction) && DatasetMatchesCloudPath(dataset.Name, task.Path)));
            })
            .ToList();
    }

    private IReadOnlyList<DataProtectionTaskStatus> MapTasks(IReadOnlyList<TrueNasSnapshotTaskDto> snapshotTasks, IReadOnlyList<TrueNasReplicationTaskDto> replicationTasks, IReadOnlyList<TrueNasCloudSyncTaskDto> cloudSyncTasks, IReadOnlyList<TrueNasJobDto> jobs, string timeZoneId, DateTimeOffset observedAt)
    {
        var mapped = new List<DataProtectionTaskStatus>();
        mapped.AddRange(snapshotTasks.Select(task => MapSnapshotTask(task, FindJob(jobs, "snapshottask", task.Id), timeZoneId, observedAt)));
        mapped.AddRange(replicationTasks.Select(task => MapReplicationTask(task, FindJob(jobs, "replication", task.Id), timeZoneId, observedAt)));
        mapped.AddRange(cloudSyncTasks.Select(task => MapCloudSyncTask(task, FindJob(jobs, "cloudsync", task.Id), timeZoneId, observedAt)));
        return mapped.OrderBy(task => task.Kind).ThenBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private DataProtectionTaskStatus MapSnapshotTask(TrueNasSnapshotTaskDto task, TrueNasJobDto? job, string timeZoneId, DateTimeOffset observedAt)
    {
        var state = StateFrom(task.State, job);
        return new DataProtectionTaskStatus(
            DataProtectionTaskKind.Snapshot,
            task.Id,
            $"Snapshot · {task.Dataset}",
            task.Dataset,
            null,
            task.Enabled,
            state,
            ProgressFrom(task.State, job),
            LastSuccessFrom(task.State, job, state),
            NextRun(task.Schedule, timeZoneId, observedAt),
            ScheduleText(task.Schedule),
            ErrorFrom(task.State, job));
    }

    private DataProtectionTaskStatus MapReplicationTask(TrueNasReplicationTaskDto task, TrueNasJobDto? job, string timeZoneId, DateTimeOffset observedAt)
    {
        var stateElement = task.Job.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? task.State : task.Job;
        var state = StateFrom(stateElement, job);
        var schedule = task.Schedule is null && task.PeriodicSnapshotTaskIds.Count > 0 ? "After snapshot task" : ScheduleText(task.Schedule);
        return new DataProtectionTaskStatus(
            DataProtectionTaskKind.Replication,
            task.Id,
            string.IsNullOrWhiteSpace(task.Name) ? $"Replication #{task.Id}" : task.Name,
            task.SourceDatasets.Count == 0 ? "—" : string.Join(", ", task.SourceDatasets),
            task.TargetDataset,
            task.Enabled,
            state,
            ProgressFrom(stateElement, job),
            LastSuccessFrom(stateElement, job, state),
            NextRun(task.Schedule, timeZoneId, observedAt),
            schedule,
            ErrorFrom(stateElement, job));
    }

    private DataProtectionTaskStatus MapCloudSyncTask(TrueNasCloudSyncTaskDto task, TrueNasJobDto? job, string timeZoneId, DateTimeOffset observedAt)
    {
        var state = StateFrom(task.Job, job);
        return new DataProtectionTaskStatus(
            DataProtectionTaskKind.CloudSync,
            task.Id,
            string.IsNullOrWhiteSpace(task.Description) ? $"Cloud sync #{task.Id}" : task.Description,
            task.Path,
            null,
            task.Enabled && !task.IsLocked,
            state,
            ProgressFrom(task.Job, job),
            LastSuccessFrom(task.Job, job, state),
            NextRun(task.Schedule, timeZoneId, observedAt),
            ScheduleText(task.Schedule),
            ErrorFrom(task.Job, job) ?? (task.IsLocked ? "Task is locked." : null));
    }

    private DateTimeOffset? NextRun(TrueNasCronScheduleDto? schedule, string timeZoneId, DateTimeOffset observedAt)
    {
        if (schedule is null)
        {
            return null;
        }

        try
        {
            return scheduleService.GetNextRun(CronExpression(schedule), timeZoneId, observedAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "TrueNAS task schedule {Schedule} could not be parsed in {TimeZoneId}", CronExpression(schedule), timeZoneId);
            return null;
        }
    }

    private static IReadOnlyList<DataProtectionWarning> BuildWarnings(IReadOnlyList<DatasetProtectionStatus> datasets, IReadOnlyList<DataProtectionTaskStatus> tasks)
    {
        var warnings = datasets
            .Where(dataset => dataset.IsUnprotected)
            .Select(dataset => new DataProtectionWarning("warning", "Unprotected dataset", $"No enabled snapshot, replication, or cloud-sync task covers {dataset.Name}.", dataset.Name))
            .ToList();

        warnings.AddRange(tasks
            .Where(task => task.IsEnabled && task.State is "FAILED" or "ERROR" or "ABORTED")
            .Select(task => new DataProtectionWarning("danger", $"{TaskKindText(task.Kind)} task failed", string.IsNullOrWhiteSpace(task.Error) ? $"{task.Name} reported {task.State}." : task.Error, task.Source)));
        return warnings;
    }

    private static TrueNasJobDto? FindJob(IReadOnlyList<TrueNasJobDto> jobs, string methodFragment, int taskId) => jobs
        .Where(job => job.Method.Contains(methodFragment, StringComparison.OrdinalIgnoreCase) && TrueNasJsonValueReader.ContainsInteger(job.Arguments, taskId))
        .OrderByDescending(job => job.Id)
        .FirstOrDefault();

    private static string StateFrom(JsonElement element, TrueNasJobDto? job)
    {
        var state = TrueNasJsonValueReader.FindString(element, "state", "status") ?? job?.State;
        return string.IsNullOrWhiteSpace(state) ? "NEVER RUN" : state.Trim().Replace('_', ' ').ToUpperInvariant();
    }

    private static double? ProgressFrom(JsonElement element, TrueNasJobDto? job)
    {
        var progress = TrueNasJsonValueReader.FindDouble(element, "percent", "progress") ?? job?.Progress?.Percent;
        return progress is null ? null : Math.Clamp(progress.Value, 0, 100);
    }

    private static DateTimeOffset? LastSuccessFrom(JsonElement element, TrueNasJobDto? job, string state)
    {
        if (state is not ("SUCCESS" or "FINISHED" or "COMPLETE" or "COMPLETED"))
        {
            return null;
        }

        return TrueNasJsonValueReader.FindDate(element, "time_finished", "datetime", "end_time", "finished_at", "last_run") ??
            (job is null ? null : TrueNasJsonValueReader.FindDate(job.TimeFinished));
    }

    private static string? ErrorFrom(JsonElement element, TrueNasJobDto? job)
    {
        var error = TrueNasJsonValueReader.FindString(element, "error", "message", "reason");
        return string.IsNullOrWhiteSpace(error) ? job?.Error : error.Trim();
    }

    private static bool DatasetMatches(string dataset, string taskDataset, bool recursive, IReadOnlyList<string> exclusions)
    {
        if (string.IsNullOrWhiteSpace(taskDataset))
        {
            return false;
        }

        var matches = string.Equals(dataset, taskDataset, StringComparison.OrdinalIgnoreCase) ||
            recursive && dataset.StartsWith($"{taskDataset}/", StringComparison.OrdinalIgnoreCase);
        return matches && !exclusions.Any(excluded => string.Equals(dataset, excluded, StringComparison.OrdinalIgnoreCase) || dataset.StartsWith($"{excluded}/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool DatasetMatchesCloudPath(string dataset, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("mnt/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        return string.Equals(normalized, dataset, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith($"{dataset}/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDataset(string dataset)
    {
        var segments = dataset.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals(".system", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("ix-applications", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("ix-apps", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("iocage", StringComparison.OrdinalIgnoreCase)) ||
            dataset.Contains("boot-pool", StringComparison.OrdinalIgnoreCase) ||
            dataset.Contains("freenas-boot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPushDirection(string direction) => string.Equals(direction, "PUSH", StringComparison.OrdinalIgnoreCase);

    private static string CronExpression(TrueNasCronScheduleDto schedule) => $"{schedule.Minute} {schedule.Hour} {schedule.DayOfMonth} {schedule.Month} {schedule.DayOfWeek}";

    private static string ScheduleText(TrueNasCronScheduleDto? schedule) => schedule is null ? "Manual" : CronExpression(schedule);

    private static string TaskKindText(DataProtectionTaskKind kind) => kind switch
    {
        DataProtectionTaskKind.Snapshot => "Snapshot",
        DataProtectionTaskKind.Replication => "Replication",
        DataProtectionTaskKind.CloudSync => "Cloud-sync",
        _ => "Protection"
    };

    private static bool IsPermissionFailure(TrueNasClientException exception) => exception.Code is "-32001" or "EACCES" or "EPERM" ||
        exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("role", StringComparison.OrdinalIgnoreCase);

    private sealed record SourceResult<T>(IReadOnlyList<T> Items, DataProtectionSourceState Source);
}
