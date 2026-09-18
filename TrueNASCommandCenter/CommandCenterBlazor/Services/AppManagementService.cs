using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

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

public sealed class AppManagementService(ITrueNasClient trueNasClient, IDbContextFactory<AppDbContext> dbFactory, TimeProvider timeProvider, ILogger<AppManagementService> logger, RunLock runLock) : IAppManagementService
{
    /// <inheritdoc cref="IAppManagementService.ExecuteAsync"/>
    public async Task<AppManagementResult> ExecuteAsync(string appId, AppLifecycleAction action, CancellationToken cancellationToken = default)
    {
        await using var lease = await runLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new AppManagementResult(false, "A check, update, or lifecycle action is already in progress. Wait for it to finish before starting another action.", ErrorCode: "OPERATION_IN_PROGRESS");
        }

        return await ExecuteCoreAsync(appId, action, AppManagementOrigin.Manual, cancellationToken);
    }

    /// <inheritdoc cref="IAppManagementService.ExecuteAutomaticRecoveryAsync"/>
    public async Task<AppManagementResult> ExecuteAutomaticRecoveryAsync(string appId, CancellationToken cancellationToken = default)
    {
        // The coordinator owns RunLock while evaluating health. Do not reacquire it here.
        return await ExecuteCoreAsync(appId, AppLifecycleAction.Restart, AppManagementOrigin.AutomaticRecovery, cancellationToken);
    }

    private async Task<AppManagementResult> ExecuteCoreAsync(string appId, AppLifecycleAction action, AppManagementOrigin origin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("An app identifier is required.", nameof(appId));
        }

        int timeoutSeconds;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var app = await db.Apps.AsNoTracking().SingleAsync(item => item.Id == appId, cancellationToken);
            if (origin == AppManagementOrigin.AutomaticRecovery && (!app.IsInstalled || app.MaintenanceMode || app.DowntimeAction != DowntimeAction.RestartAndNotify))
            {
                return new AppManagementResult(false, "Automatic recovery is no longer enabled for this app.", ErrorCode: "RECOVERY_DEFERRED");
            }

            timeoutSeconds = await db.Settings.Where(item => item.Id == 1).Select(item => item.VerificationTimeoutSeconds).SingleAsync(cancellationToken);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var operationToken = operationCancellation.Token;
        var run = await CreateAuditRunAsync(origin, cancellationToken);
        var started = timeProvider.GetUtcNow().UtcDateTime;
        long? jobId = null;
        string? jobState = null;
        try
        {
            if (origin == AppManagementOrigin.AutomaticRecovery)
            {
                var current = await trueNasClient.GetAppAsync(appId, operationToken);
                var health = AppDiscoveryService.DetermineHealth(current.State, current.ActiveWorkloads);
                if (health is AppHealthState.Unknown or AppHealthState.Running)
                {
                    var deferredMessage = "Recovery was deferred because a fresh TrueNAS check shows the app is already running or changing state. No stop or start was requested.";
                    await CompleteAuditAsync(run, appId, action, origin, started, false, deferredMessage, null, "RECOVERY_DEFERRED", null, CancellationToken.None, skipped: true);
                    return new AppManagementResult(false, deferredMessage, current.State, "RECOVERY_DEFERRED");
                }

                // Starting an already stopped app avoids a redundant stop job and cannot
                // interrupt database initialization that begins between inventory refreshes.
                if (health == AppHealthState.Stopped)
                {
                    action = AppLifecycleAction.Start;
                }
            }

            if (action == AppLifecycleAction.Restart)
            {
                await ExecuteJobAsync(token => trueNasClient.StopAppAsync(appId, token));
                await VerifyStateAsync(appId, AppLifecycleAction.Stop, operationToken);
                await ExecuteJobAsync(token => trueNasClient.StartAppAsync(appId, token));
            }
            else
            {
                Func<CancellationToken, Task<long>> startJob = action switch
                {
                    AppLifecycleAction.Start => token => trueNasClient.StartAppAsync(appId, token),
                    AppLifecycleAction.Stop => token => trueNasClient.StopAppAsync(appId, token),
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported app lifecycle action.")
                };
                await ExecuteJobAsync(startJob);
            }

            var source = await VerifyStateAsync(appId, action, operationToken);
            await PersistStateAsync(source, action, origin, cancellationToken);
            var message = $"{source.Name} was {PastTense(action)} successfully.";
            await CompleteAuditAsync(run, appId, action, origin, started, true, message, jobId, null, jobState, cancellationToken);
            return new AppManagementResult(true, message, source.State);
        }
        catch (OperationCanceledException)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            var code = cancelled ? "CANCELLED" : "LIFECYCLE_VERIFICATION_TIMEOUT";
            var message = cancelled ? "The lifecycle action was cancelled before completion could be verified." : "TrueNAS did not finish the lifecycle action and reach the expected healthy state within the verification timeout. The job may still be running; check TrueNAS before retrying.";
            await CompleteAuditAsync(run, appId, action, origin, started, false, message, jobId, code, jobState, CancellationToken.None, cancelled: cancelled);
            return new AppManagementResult(false, message, ErrorCode: code);
        }
        catch (TrueNasClientException exception)
        {
            logger.LogWarning(exception, "TrueNAS app lifecycle action {Action} failed for {AppId} with {ErrorCode}", action, appId, exception.Code);
            var message = $"TrueNAS could not {Verb(action)} the app: {exception.Message}";
            await CompleteAuditAsync(run, appId, action, origin, started, false, message, jobId, exception.Code, jobState, CancellationToken.None);
            return new AppManagementResult(false, message, ErrorCode: exception.Code);
        }

        async Task ExecuteJobAsync(Func<CancellationToken, Task<long>> startJob)
        {
            jobId = null;
            jobState = null;
            jobId = await startJob(operationToken);
            await trueNasClient.WaitForJobAsync(jobId.Value, operationToken);
            jobState = "SUCCESS";
        }
    }

    private async Task<TrueNasAppDto> VerifyStateAsync(string appId, AppLifecycleAction action, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await trueNasClient.GetAppAsync(appId, cancellationToken);
            var reachedState = action == AppLifecycleAction.Stop
                ? string.Equals(source.State, "STOPPED", StringComparison.OrdinalIgnoreCase)
                : AppDiscoveryService.DetermineHealth(source.State, source.ActiveWorkloads) == AppHealthState.Running;
            if (reachedState)
            {
                return source;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
        }
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
        app.HealthState = AppDiscoveryService.DetermineHealth(source.State, source.ActiveWorkloads, app.MaintenanceMode);
        app.HealthMessage = AppDiscoveryService.HealthMessage(app.HealthState);
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

    private async Task CompleteAuditAsync(UpdateRun run, string appId, AppLifecycleAction action, AppManagementOrigin origin, DateTime started, bool success, string message, long? jobId, string? errorCode, string? jobState, CancellationToken cancellationToken, bool cancelled = false, bool skipped = false)
    {
        var ended = timeProvider.GetUtcNow().UtcDateTime;
        run.EndedUtc = ended;
        run.CheckedCount = 1;
        run.EligibleCount = 1;
        run.SucceededCount = success ? 1 : 0;
        run.FailedCount = success || cancelled || skipped ? 0 : 1;
        run.SkippedCount = skipped ? 1 : 0;
        run.Status = skipped ? RunStatus.Skipped : cancelled ? RunStatus.Cancelled : success ? RunStatus.Succeeded : RunStatus.Failed;
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
            Status = skipped ? AttemptStatus.Skipped : cancelled ? AttemptStatus.Cancelled : success ? AttemptStatus.Succeeded : AttemptStatus.Failed,
            ReasonCode = success ? "LIFECYCLE_SUCCEEDED" : errorCode ?? "LIFECYCLE_FAILED",
            ReasonMessage = message,
            TrueNasJobId = jobId,
            TrueNasJobState = jobState ?? errorCode switch { "JOB_FAILED" or "REGISTRY_RATE_LIMIT" => "FAILED", "JOB_ABORTED" => "ABORTED", _ => "UNKNOWN" }
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
