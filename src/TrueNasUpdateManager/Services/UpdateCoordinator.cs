using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Integrations.TrueNas;
using TrueNasUpdateManager.Notifications;

namespace TrueNasUpdateManager.Services;

public interface IUpdateCoordinator
{
    Task<RunResult> RunAsync(
        RunTrigger trigger,
        bool executeUpdates,
        string? appId = null,
        bool riskyStateConfirmed = false,
        CancellationToken cancellationToken = default);

    Task<RunResult> RollbackAsync(
        string appId,
        string targetVersion,
        CancellationToken cancellationToken = default);
}

public sealed class UpdateCoordinator(
    RunLock runLock,
    IAppDiscoveryService discoveryService,
    ITrueNasClient trueNasClient,
    IUpdatePolicyEvaluator policyEvaluator,
    IUpdateExecutor updateExecutor,
    INotificationDispatcher notifications,
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    TimeProvider timeProvider,
    ILogger<UpdateCoordinator> logger) : IUpdateCoordinator
{
    public async Task<RunResult> RunAsync(
        RunTrigger trigger,
        bool executeUpdates,
        string? appId = null,
        bool riskyStateConfirmed = false,
        CancellationToken cancellationToken = default)
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
            var settings = await settingsService.GetRecordAsync(cancellationToken);
            var apps = appId is null
                ? await discoveryService.DiscoverAsync(cancellationToken)
                : [await discoveryService.DiscoverAppAsync(appId, cancellationToken)];
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
            return ToResult(run, "Run completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            run.ErrorSummary = "The run was cancelled.";
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await SaveRunAsync(run, CancellationToken.None);
            return ToResult(run, run.ErrorSummary);
        }
        catch (Exception exception)
        {
            var (code, message) = SanitizeFailure(exception);
            run.Status = RunStatus.Failed;
            run.FailedCount++;
            run.ErrorSummary = message;
            run.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await SaveRunAsync(run, CancellationToken.None);
            logger.LogWarning("Run {RunId} failed with {ReasonCode}", run.Id, code);

            var eventType = exception is TrueNasClientException or InvalidOperationException
                ? NotificationEventType.TrueNasConnectionFailed
                : NotificationEventType.ScheduledCheckFailed;
            if (trigger == RunTrigger.Scheduled && eventType == NotificationEventType.TrueNasConnectionFailed)
            {
                await DispatchSystemEventAsync(
                    NotificationEventType.ScheduledCheckFailed,
                    code,
                    message,
                    CancellationToken.None);
            }

            await DispatchSystemEventAsync(eventType, code, message, CancellationToken.None);
            return ToResult(run, message);
        }
    }

    public async Task<RunResult> RollbackAsync(
        string appId,
        string targetVersion,
        CancellationToken cancellationToken = default)
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
            await SaveRunAsync(run, CancellationToken.None);
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

    private async Task RecordDecisionAsync(
        Guid runId,
        AppRecord app,
        UpdateDecision decision,
        CancellationToken cancellationToken)
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

    private Task DispatchUpdateEventAsync(
        AppRecord app,
        NotificationEventType eventType,
        string reasonCode,
        string message,
        string? target,
        CancellationToken cancellationToken)
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

    private Task DispatchSystemEventAsync(
        NotificationEventType eventType,
        string reasonCode,
        string message,
        CancellationToken cancellationToken)
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

    private async Task SaveRunAndScheduleStateAsync(
        UpdateRun run,
        RunTrigger trigger,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateRuns.Update(run);
        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
        settings.LastCompletedCheckUtc = run.EndedUtc;
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

    private static (string Code, string Message) SanitizeFailure(Exception exception) =>
        exception switch
        {
            TrueNasClientException trueNas => (trueNas.Code, Truncate(trueNas.Message)),
            InvalidOperationException configuration => ("CONFIGURATION_ERROR", Truncate(configuration.Message)),
            DbUpdateException => ("DATABASE_ERROR", "The run stopped because audit state could not be persisted."),
            _ => ("RUN_FAILED", "The run failed unexpectedly.")
        };

    private static bool IsServerWideFailure(string reasonCode) =>
        reasonCode is "NETWORK_ERROR" or
            "CONNECTION_CLOSED" or
            "AUTHENTICATION_FAILED" or
            "TLS_FAILURE" or
            "TIMEOUT";

    private static string Truncate(string value) => value.Length <= 1024 ? value : value[..1024];
}
