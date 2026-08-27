using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;

namespace TrueNasAppManager.Services;

public interface IAppManagementService
{
    /// <summary>Executes a lifecycle action for an installed TrueNAS app and refreshes its persisted state.</summary>
    /// <param name="appId">The TrueNAS app identifier.</param>
    /// <param name="action">The lifecycle action to execute.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The result reported to the user.</returns>
    Task<AppManagementResult> ExecuteAsync(string appId, AppLifecycleAction action, CancellationToken cancellationToken = default);
    /// <summary>Attempts a single automatic recovery without placing the app into maintenance mode.</summary>
    /// <param name="appId">The TrueNAS app identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The result reported to health monitoring.</returns>
    Task<AppManagementResult> ExecuteAutomaticRecoveryAsync(string appId, CancellationToken cancellationToken = default);
}

public sealed class AppManagementService(ITrueNasClient trueNasClient, IDbContextFactory<AppDbContext> dbFactory, TimeProvider timeProvider, ILogger<AppManagementService> logger) : IAppManagementService
{
    /// <inheritdoc cref="IAppManagementService.ExecuteAsync"/>
    public async Task<AppManagementResult> ExecuteAsync(string appId, AppLifecycleAction action, CancellationToken cancellationToken = default)
    {
        return await ExecuteCoreAsync(appId, action, AppManagementOrigin.Manual, cancellationToken);
    }

    /// <inheritdoc cref="IAppManagementService.ExecuteAutomaticRecoveryAsync"/>
    public async Task<AppManagementResult> ExecuteAutomaticRecoveryAsync(string appId, CancellationToken cancellationToken = default)
    {
        return await ExecuteCoreAsync(appId, AppLifecycleAction.Restart, AppManagementOrigin.AutomaticRecovery, cancellationToken);
    }

    private async Task<AppManagementResult> ExecuteCoreAsync(string appId, AppLifecycleAction action, AppManagementOrigin origin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("An app identifier is required.", nameof(appId));
        }

        var run = await CreateAuditRunAsync(origin, cancellationToken);
        var started = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            long? jobId = null;
            if (action == AppLifecycleAction.Restart)
            {
                jobId = await ExecuteJobAsync(() => trueNasClient.StopAppAsync(appId, cancellationToken), cancellationToken);
                jobId = await ExecuteJobAsync(() => trueNasClient.StartAppAsync(appId, cancellationToken), cancellationToken);
            }
            else
            {
                Func<Task<long>> startJob = action switch
                {
                    AppLifecycleAction.Start => () => trueNasClient.StartAppAsync(appId, cancellationToken),
                    AppLifecycleAction.Stop => () => trueNasClient.StopAppAsync(appId, cancellationToken),
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported app lifecycle action.")
                };
                jobId = await ExecuteJobAsync(startJob, cancellationToken);
            }

            var source = await trueNasClient.GetAppAsync(appId, cancellationToken);
            await PersistStateAsync(source, action, origin, cancellationToken);
            var message = $"{source.Name} was {PastTense(action)} successfully.";
            await CompleteAuditAsync(run, appId, action, origin, started, true, message, jobId, null, cancellationToken);
            return new AppManagementResult(true, message, source.State);
        }
        catch (TrueNasClientException exception)
        {
            logger.LogWarning(exception, "TrueNAS app lifecycle action {Action} failed for {AppId} with {ErrorCode}", action, appId, exception.Code);
            var message = $"TrueNAS could not {Verb(action)} the app: {exception.Message}";
            await CompleteAuditAsync(run, appId, action, origin, started, false, message, null, exception.Code, CancellationToken.None);
            return new AppManagementResult(false, message, ErrorCode: exception.Code);
        }
    }

    private async Task<long> ExecuteJobAsync(Func<Task<long>> startJob, CancellationToken cancellationToken)
    {
        var jobId = await startJob();
        await trueNasClient.WaitForJobAsync(jobId, cancellationToken);
        return jobId;
    }

    private async Task PersistStateAsync(TrueNasAppDto source, AppLifecycleAction action, AppManagementOrigin origin, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Apps.SingleAsync(item => item.Id == source.Id, cancellationToken);
        app.State = source.State;
        app.LastSeenUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (origin == AppManagementOrigin.Manual)
        {
            app.MaintenanceMode = action == AppLifecycleAction.Stop;
        }

        var isDown = string.Equals(source.State, "STOPPED", StringComparison.OrdinalIgnoreCase) || string.Equals(source.State, "CRASHED", StringComparison.OrdinalIgnoreCase);
        app.DowntimeNotificationActive = origin != AppManagementOrigin.Manual && app.NotifyOnDowntime && isDown;
        if (origin == AppManagementOrigin.Manual)
        {
            app.HealthIncidentId = null;
            app.RecoveryAttemptedUtc = null;
        }
        app.HealthState = app.MaintenanceMode ? AppHealthState.Maintenance : isDown ? AppHealthState.Stopped : AppHealthState.Running;
        app.HealthMessage = app.MaintenanceMode ? "Monitoring is paused for an intentional maintenance stop." : isDown ? "The app is stopped or crashed." : "The app is running.";
        if (isDown)
        {
            app.StatusLabel = string.Equals(source.State, "CRASHED", StringComparison.OrdinalIgnoreCase) ? "Crashed" : "Stopped";
        }
        else if (app.ActionRequired)
        {
            app.StatusLabel = "Blocked: action required";
        }
        else if (app.CatalogUpdateAvailable)
        {
            app.StatusLabel = "Update available";
        }
        else if (app.ImageUpdateAvailable)
        {
            app.StatusLabel = "Image update";
        }
        else
        {
            app.StatusLabel = "Up to date";
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<UpdateRun> CreateAuditRunAsync(AppManagementOrigin origin, CancellationToken cancellationToken)
    {
        var run = new UpdateRun
        {
            Trigger = origin == AppManagementOrigin.AutomaticRecovery ? RunTrigger.HealthRecovery : RunTrigger.Lifecycle,
            StartedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task CompleteAuditAsync(UpdateRun run, string appId, AppLifecycleAction action, AppManagementOrigin origin, DateTime started, bool success, string message, long? jobId, string? errorCode, CancellationToken cancellationToken)
    {
        var ended = timeProvider.GetUtcNow().UtcDateTime;
        run.EndedUtc = ended;
        run.CheckedCount = 1;
        run.EligibleCount = 1;
        run.SucceededCount = success ? 1 : 0;
        run.FailedCount = success ? 0 : 1;
        run.Status = success ? RunStatus.Succeeded : RunStatus.Failed;
        run.ErrorSummary = success ? null : message;
        var attempt = new UpdateAttempt
        {
            RunId = run.Id,
            AppId = appId,
            Kind = origin == AppManagementOrigin.AutomaticRecovery ? AttemptKind.AutomaticRecovery : action switch
            {
                AppLifecycleAction.Start => AttemptKind.LifecycleStart,
                AppLifecycleAction.Stop => AttemptKind.LifecycleStop,
                AppLifecycleAction.Restart => AttemptKind.LifecycleRestart,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported lifecycle action.")
            },
            StartedUtc = started,
            EndedUtc = ended,
            Status = success ? AttemptStatus.Succeeded : AttemptStatus.Failed,
            ReasonCode = success ? "LIFECYCLE_SUCCEEDED" : errorCode ?? "LIFECYCLE_FAILED",
            ReasonMessage = message,
            TrueNasJobId = jobId,
            TrueNasJobState = success ? "SUCCESS" : "FAILED"
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateRuns.Update(run);
        db.UpdateAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Verb(AppLifecycleAction action) => action switch
    {
        AppLifecycleAction.Start => "start",
        AppLifecycleAction.Stop => "stop",
        AppLifecycleAction.Restart => "restart",
        _ => "manage"
    };

    private static string PastTense(AppLifecycleAction action) => action switch
    {
        AppLifecycleAction.Start => "started",
        AppLifecycleAction.Stop => "stopped",
        AppLifecycleAction.Restart => "restarted",
        _ => "managed"
    };
}
