using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Notifications;

namespace TrueNasAppManager.Services;

public interface IUpdateCoordinator
{
    /// <summary>Refreshes the complete TrueNAS application inventory without evaluating updates.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The inventory run result.</returns>
    Task<RunResult> RefreshAppsAsync(CancellationToken cancellationToken = default);
    /// <summary>Refreshes inventory, evaluates updates, and optionally executes eligible updates.</summary>
    /// <param name="trigger">The manual or scheduled run trigger.</param>
    /// <param name="executeUpdates">Whether eligible updates should execute.</param>
    /// <param name="appId">An optional application to evaluate after the full inventory refresh.</param>
    /// <param name="riskyStateConfirmed">Whether the user confirmed a risky manual update.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The update run result.</returns>
    Task<RunResult> CheckAndUpdateAsync(RunTrigger trigger, bool executeUpdates, string? appId = null, bool riskyStateConfirmed = false, CancellationToken cancellationToken = default);
    /// <summary>Runs the legacy check/update entry point while preserving refresh-first ordering.</summary>
    /// <param name="trigger">The run trigger.</param>
    /// <param name="executeUpdates">Whether eligible updates should execute.</param>
    /// <param name="appId">An optional application to evaluate.</param>
    /// <param name="riskyStateConfirmed">Whether the user confirmed a risky manual update.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The run result.</returns>
    Task<RunResult> RunAsync(RunTrigger trigger, bool executeUpdates, string? appId = null, bool riskyStateConfirmed = false, CancellationToken cancellationToken = default);
    /// <summary>Rolls one application back to a previously available version.</summary>
    /// <param name="appId">The application identifier.</param>
    /// <param name="targetVersion">The version to restore.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The rollback run result.</returns>
    Task<RunResult> RollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default);
}

public sealed class UpdateCoordinator(
    RunLock runLock,
    IAppDiscoveryService discoveryService,
    IAppHealthMonitorService healthMonitorService,
    IGitHubMetadataService gitHubMetadataService,
    ITrueNasClient trueNasClient,
    IUpdatePolicyEvaluator policyEvaluator,
    IUpdateExecutor updateExecutor,
    INotificationDispatcher notifications,
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    TimeProvider timeProvider,
    ILogger<UpdateCoordinator> logger) : IUpdateCoordinator
{
    public Task<RunResult> RefreshAppsAsync(CancellationToken cancellationToken = default) => RunAsync(RunTrigger.RefreshApps, false, cancellationToken: cancellationToken);

    public Task<RunResult> CheckAndUpdateAsync(RunTrigger trigger, bool executeUpdates, string? appId = null, bool riskyStateConfirmed = false, CancellationToken cancellationToken = default) => RunAsync(trigger, executeUpdates, appId, riskyStateConfirmed, cancellationToken);

    /// <inheritdoc cref="IUpdateCoordinator.RunAsync"/>
    public async Task<RunResult> RunAsync(RunTrigger trigger, bool executeUpdates, string? appId = null, bool riskyStateConfirmed = false, CancellationToken cancellationToken = default)
    {
        var run = await CreateRunAsync(trigger, cancellationToken);
        await using var lease = await runLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            run.Status = RunStatus.Skipped;
            run.SkippedCount = 1;
            run.ErrorSummary = "Another check or update run is already active.";
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await SaveRunAsync(run, cancellationToken);
            return ToResult(run, run.ErrorSummary);
        }

        try
        {
            var refresh = await discoveryService.RefreshAsync(cancellationToken);
            _ = await healthMonitorService.EvaluateAsync(refresh.AppIds, cancellationToken);
            if (trigger == RunTrigger.RefreshApps)
            {
                run.CheckedCount = refresh.Discovered;
                run.Status = RunStatus.Succeeded;
                run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
                await SaveRunAsync(run, cancellationToken);
                _ = gitHubMetadataService.RefreshStaleAsync(refresh.AppIds, CancellationToken.None);
                return ToResult(run, $"Refreshed {refresh.Discovered} apps; {refresh.Missing} previously known apps are no longer installed.");
            }

            var settings = await settingsService.GetRecordAsync(cancellationToken);
            await using var inventoryDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            var query = inventoryDb.Apps.AsNoTracking().Where(app => app.IsInstalled);
            var apps = appId is null ? await query.OrderBy(app => app.Name).ToListAsync(cancellationToken) : [await query.SingleAsync(app => app.Id == appId, cancellationToken)];
            run.CheckedCount = apps.Count;

            for (var appIndex = 0; appIndex < apps.Count; appIndex++)
            {
                var app = apps[appIndex];
                cancellationToken.ThrowIfCancellationRequested();
                var manual = trigger == RunTrigger.UpdateNow;
                var target = app.CatalogUpdateAvailable ? app.LatestVersion : null;
                var managerAppId = settings.ManagerAppId ??
                                   Environment.GetEnvironmentVariable("TRUENAS_APP_ID") ??
                                   Environment.GetEnvironmentVariable("IX_APP_NAME");
                var decision = policyEvaluator.Evaluate(
                    app,
                    target,
                    manual,
                    riskyStateConfirmed,
                    managerAppId);
                if (decision.Kind == UpdateDecisionKind.Eligible &&
                    executeUpdates &&
                    trueNasClient.HasWriteAccess is false)
                {
                    decision = new UpdateDecision(
                        UpdateDecisionKind.Blocked,
                        "MISSING_WRITE_ACCESS",
                        "The authenticated TrueNAS account does not expose APPS_WRITE.",
                        target);
                }

                if (decision.Kind == UpdateDecisionKind.Eligible && executeUpdates)
                {
                    run.EligibleCount++;
                    var outcome = await updateExecutor.ExecuteAsync(run.Id, app, target, cancellationToken);
                    if (outcome.Status == AttemptStatus.Succeeded)
                    {
                        run.SucceededCount++;
                        if (manual || ResolveSuccessNotification(app, settings))
                        {
                            await DispatchUpdateEventAsync(
                                app,
                                NotificationEventType.AutomaticUpdateSucceeded,
                                outcome.ReasonCode,
                                outcome.Message,
                                target,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        run.FailedCount++;
                        await DispatchUpdateEventAsync(
                            app,
                            NotificationEventType.AutomaticUpdateFailed,
                            outcome.ReasonCode,
                            outcome.Message,
                            target,
                            cancellationToken);
                        if (IsServerWideFailure(outcome.ReasonCode))
                        {
                            for (var skippedIndex = appIndex + 1; skippedIndex < apps.Count; skippedIndex++)
                            {
                                var skippedApp = apps[skippedIndex];
                                run.SkippedCount++;
                                await RecordDecisionAsync(
                                    run.Id,
                                    skippedApp,
                                    new UpdateDecision(
                                        UpdateDecisionKind.Blocked,
                                        "SERVER_CONDITION",
                                        "Skipped because a server-wide failure made further updates unsafe.",
                                        skippedApp.LatestVersion),
                                    cancellationToken);
                            }

                            break;
                        }
                    }

                    continue;
                }

                if (decision.Kind == UpdateDecisionKind.Eligible && !executeUpdates)
                {
                    continue;
                }

                if (decision.Kind is not UpdateDecisionKind.NoUpdate)
                {
                    run.SkippedCount++;
                    await RecordDecisionAsync(run.Id, app, decision, cancellationToken);
                }

                if (decision.Kind is UpdateDecisionKind.Notify or UpdateDecisionKind.ManualApproval)
                {
                    await DispatchUpdateEventAsync(
                        app,
                        NotificationEventType.ManualApprovalAvailable,
                        decision.ReasonCode,
                        decision.Message,
                        target,
                        cancellationToken);
                }
                else if (decision.Kind == UpdateDecisionKind.Blocked && app.Policy == AppPolicy.AutoUpdate)
                {
                    await DispatchUpdateEventAsync(
                        app,
                        NotificationEventType.AutomaticUpdateBlocked,
                        decision.ReasonCode,
                        decision.Message,
                        target,
                        cancellationToken);
                }
            }

            run.Status = CalculateStatus(run);
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await SaveRunAndScheduleStateAsync(run, trigger, cancellationToken);
            _ = gitHubMetadataService.RefreshStaleAsync(refresh.AppIds, CancellationToken.None);
            return ToResult(run, "Run completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            run.ErrorSummary = "The run was cancelled.";
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await TrySaveRunAsync(run);
            return ToResult(run, run.ErrorSummary);
        }
        catch (Exception exception)
        {
            var (code, message) = SanitizeFailure(exception);
            run.Status = RunStatus.Failed;
            run.FailedCount++;
            run.ErrorSummary = message;
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await TrySaveRunAsync(run);
            logger.LogWarning(exception, "Run {RunId} failed with {ReasonCode}", run.Id, code);

            if (exception is TrueNasClientException or InvalidOperationException)
            {
                await DispatchSystemEventAsync(
                    NotificationEventType.TrueNasConnectionFailed,
                    code,
                    message,
                    CancellationToken.None);
            }
            else if (trigger == RunTrigger.Scheduled)
            {
                await DispatchSystemEventAsync(
                    NotificationEventType.ScheduledCheckFailed,
                    code,
                    message,
                    CancellationToken.None);
            }

            return ToResult(run, message);
        }
    }

    /// <inheritdoc cref="IUpdateCoordinator.RollbackAsync"/>
    public async Task<RunResult> RollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default)
    {
        var run = await CreateRunAsync(RunTrigger.Rollback, cancellationToken);
        await using var lease = await runLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            run.Status = RunStatus.Skipped;
            run.SkippedCount = 1;
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            run.ErrorSummary = "Another check or update run is already active.";
            await SaveRunAsync(run, cancellationToken);
            return ToResult(run, run.ErrorSummary);
        }

        try
        {
            var app = await discoveryService.DiscoverAppAsync(appId, cancellationToken);
            run.CheckedCount = 1;
            run.EligibleCount = 1;
            var outcome = await updateExecutor.RollbackAsync(run.Id, app, targetVersion, cancellationToken);
            if (outcome.Status == AttemptStatus.Succeeded)
            {
                run.SucceededCount = 1;
                run.Status = RunStatus.Succeeded;
                await DispatchUpdateEventAsync(
                    app,
                    NotificationEventType.RollbackOccurred,
                    outcome.ReasonCode,
                    outcome.Message,
                    targetVersion,
                    cancellationToken);
            }
            else
            {
                run.FailedCount = 1;
                run.Status = RunStatus.Failed;
                run.ErrorSummary = outcome.Message;
            }

            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await SaveRunAsync(run, cancellationToken);
            return ToResult(run, outcome.Message);
        }
        catch (Exception exception)
        {
            var (_, message) = SanitizeFailure(exception);
            run.Status = RunStatus.Failed;
            run.FailedCount = 1;
            run.ErrorSummary = message;
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await TrySaveRunAsync(run);
            logger.LogWarning(exception, "Rollback run {RunId} failed", run.Id);
            return ToResult(run, message);
        }
    }

    private async Task<UpdateRun> CreateRunAsync(RunTrigger trigger, CancellationToken cancellationToken)
    {
        var run = new UpdateRun
        {
            Trigger = trigger,
            StartedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task RecordDecisionAsync(Guid runId, AppRecord app, UpdateDecision decision, CancellationToken cancellationToken)
    {
        var status = decision.Kind == UpdateDecisionKind.Blocked &&
                     decision.ReasonCode != "SERVER_CONDITION"
            ? AttemptStatus.Blocked
            : AttemptStatus.Skipped;
        var attempt = new UpdateAttempt
        {
            RunId = runId,
            AppId = app.Id,
            Kind = app.CatalogUpdateAvailable ? AttemptKind.CatalogUpgrade : AttemptKind.ImageRefresh,
            FromVersion = app.InstalledVersion,
            ToVersion = decision.TargetVersion,
            OutdatedImagesJson = app.OutdatedImagesJson,
            PolicyAtExecution = app.Policy,
            ScopeAtExecution = app.VersionScope,
            SnapshotRequested = app.SnapshotHostPaths,
            StartedUtc = timeProvider.GetUtcNow().UtcDateTime,
            EndedUtc = timeProvider.GetUtcNow().UtcDateTime,
            Status = status,
            ReasonCode = decision.ReasonCode,
            ReasonMessage = decision.Message
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task DispatchUpdateEventAsync(AppRecord app, NotificationEventType eventType, string reasonCode, string message, string? target, CancellationToken cancellationToken)
    {
        var targetOrImages = target ?? app.OutdatedImagesJson ?? "image-update";
        var dedupe = $"{eventType}|{app.Id}|{targetOrImages}|{reasonCode}";
        var notification = new NotificationEvent(
            Guid.NewGuid(),
            eventType,
            timeProvider.GetUtcNow().UtcDateTime,
            dedupe,
            $"{eventType}: {app.Name}",
            message,
            reasonCode,
            app.Id,
            app.Name,
            app.InstalledVersion,
            targetOrImages);
        return notifications.DispatchAsync(notification, cancellationToken);
    }

    private Task DispatchSystemEventAsync(NotificationEventType eventType, string reasonCode, string message, CancellationToken cancellationToken)
    {
        var notification = new NotificationEvent(
            Guid.NewGuid(),
            eventType,
            timeProvider.GetUtcNow().UtcDateTime,
            $"{eventType}|server||{reasonCode}",
            eventType.ToString(),
            message,
            reasonCode);
        return notifications.DispatchAsync(notification, cancellationToken);
    }

    private async Task SaveRunAndScheduleStateAsync(UpdateRun run, RunTrigger trigger, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateRuns.Update(run);
        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
        if (trigger != RunTrigger.RefreshApps)
        {
            settings.LastCompletedCheckUtc = run.EndedUtc;
        }
        if (trigger == RunTrigger.Scheduled)
        {
            settings.LastScheduledRunUtc = run.StartedUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveRunAsync(UpdateRun run, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateRuns.Update(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task TrySaveRunAsync(UpdateRun run)
    {
        try
        {
            await SaveRunAsync(run, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist terminal state for run {RunId}", run.Id);
        }
    }

    private static RunStatus CalculateStatus(UpdateRun run)
    {
        if (run.FailedCount == 0)
        {
            return RunStatus.Succeeded;
        }

        return run.SucceededCount > 0 ? RunStatus.PartiallySucceeded : RunStatus.Failed;
    }

    private static bool ResolveSuccessNotification(AppRecord app, SettingsRecord settings) =>
        app.NotifySuccessOverride ?? settings.NotifyAutomaticSuccess;

    private static RunResult ToResult(UpdateRun run, string message) =>
        new(
            run.Id,
            run.Status,
            message,
            run.CheckedCount,
            run.SucceededCount,
            run.FailedCount,
            run.SkippedCount);

    private static (string Code, string Message) SanitizeFailure(Exception exception)
    {
        if (exception is DbUpdateException databaseFailure)
        {
            return FindSqliteException(databaseFailure)?.SqliteErrorCode switch
            {
                5 or 6 => ("DATABASE_BUSY", "The local database stayed busy too long. Try again; if this repeats, restart TrueNAS App Manager."),
                8 => ("DATABASE_READ_ONLY", "The /data storage volume is not writable. Verify its permissions, then restart TrueNAS App Manager."),
                13 => ("DATABASE_FULL", "The TrueNAS storage used by the app is full. Free space, then try again."),
                19 => ("DATABASE_CONSTRAINT", "The refreshed app data conflicted with the local database. Check the container logs for the detailed SQLite error."),
                _ => ("DATABASE_ERROR", "The local database could not save this run. Check the container logs for the detailed SQLite error.")
            };
        }

        return exception switch
        {
            TrueNasClientException trueNas => (trueNas.Code, Truncate(trueNas.Message)),
            InvalidOperationException configuration => ("CONFIGURATION_ERROR", Truncate(configuration.Message)),
            _ => ("RUN_FAILED", "The run failed unexpectedly.")
        };
    }

    private static SqliteException? FindSqliteException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqliteException)
            {
                return sqliteException;
            }
        }

        return null;
    }

    private static bool IsServerWideFailure(string reasonCode) =>
        reasonCode is "NETWORK_ERROR" or
            "CONNECTION_CLOSED" or
            "AUTHENTICATION_FAILED" or
            "TLS_FAILURE" or
            "TIMEOUT";

    private static string Truncate(string value) => value.Length <= 1024 ? value : value[..1024];
}
