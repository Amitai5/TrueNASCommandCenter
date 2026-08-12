using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Integrations.TrueNas;

namespace TrueNasUpdateManager.Services;

public sealed record AttemptOutcome(
    Guid AttemptId,
    AttemptStatus Status,
    string ReasonCode,
    string Message,
    long? JobId = null);

public interface IUpdateExecutor
{
    Task<AttemptOutcome> ExecuteAsync(
        Guid runId,
        AppRecord app,
        string? targetVersion,
        CancellationToken cancellationToken = default);

    Task<AttemptOutcome> RollbackAsync(
        Guid runId,
        AppRecord app,
        string targetVersion,
        CancellationToken cancellationToken = default);
}

public sealed class UpdateExecutor(
    ITrueNasClient trueNasClient,
    IAppDiscoveryService discoveryService,
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    TimeProvider timeProvider,
    ILogger<UpdateExecutor> logger) : IUpdateExecutor
{
    public async Task<AttemptOutcome> ExecuteAsync(
        Guid runId,
        AppRecord app,
        string? targetVersion,
        CancellationToken cancellationToken = default)
    {
        var kind = app.CatalogUpdateAvailable ? AttemptKind.CatalogUpgrade : AttemptKind.ImageRefresh;
        var attempt = await CreateAttemptAsync(runId, app, kind, targetVersion, cancellationToken);
        try
        {
            await SetAppStatusAsync(app.Id, "Updating", null, cancellationToken);
            long jobId;
            if (kind == AttemptKind.CatalogUpgrade)
            {
                if (string.IsNullOrWhiteSpace(targetVersion))
                {
                    throw new InvalidOperationException("A catalog upgrade requires an explicit target version.");
                }

                jobId = await trueNasClient.StartUpgradeAsync(
                    app.Id,
                    targetVersion,
                    app.SnapshotHostPaths,
                    cancellationToken);
            }
            else
            {
                jobId = await trueNasClient.StartImageRefreshAsync(app.Id, cancellationToken);
            }

            attempt.TrueNasJobId = jobId;
            attempt.Status = AttemptStatus.Running;
            attempt.ReasonCode = "JOB_STARTED";
            attempt.ReasonMessage = "TrueNAS accepted the update job.";
            await SaveAttemptAsync(attempt, cancellationToken);

            var settings = await settingsService.GetRecordAsync(cancellationToken);
            using var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verificationCancellation.CancelAfter(TimeSpan.FromSeconds(settings.VerificationTimeoutSeconds));

            await trueNasClient.WaitForJobAsync(jobId, verificationCancellation.Token);
            attempt.Status = AttemptStatus.Verifying;
            attempt.TrueNasJobState = "SUCCESS";
            await SaveAttemptAsync(attempt, cancellationToken);
            await SetAppStatusAsync(app.Id, "Verifying", null, cancellationToken);

            await VerifyAsync(app, kind, targetVersion, verificationCancellation.Token);
            var updated = await discoveryService.DiscoverAppAsync(app.Id, cancellationToken);
            await SetSuccessAsync(updated.Id, attempt, cancellationToken);
            return new AttemptOutcome(attempt.Id, AttemptStatus.Succeeded, "VERIFIED", "Update verified.", jobId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SetFailureAsync(app.Id, attempt, "CANCELLED", "The update was cancelled.", null, cancellationToken);
            return new AttemptOutcome(attempt.Id, AttemptStatus.Cancelled, "CANCELLED", "The update was cancelled.");
        }
        catch (Exception exception)
        {
            var (code, message) = SanitizeException(exception);
            logger.LogWarning(
                "Update failed for app {AppId}; run {RunId}; reason {ReasonCode}",
                app.Id,
                runId,
                code);
            await SetFailureAsync(app.Id, attempt, code, message, exception, CancellationToken.None);
            return new AttemptOutcome(attempt.Id, AttemptStatus.Failed, code, message, attempt.TrueNasJobId);
        }
    }

    public async Task<AttemptOutcome> RollbackAsync(
        Guid runId,
        AppRecord app,
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        var versions = await trueNasClient.GetRollbackVersionsAsync(app.Id, cancellationToken);
        if (!versions.Contains(targetVersion, StringComparer.Ordinal))
        {
            return new AttemptOutcome(
                Guid.Empty,
                AttemptStatus.Blocked,
                "INVALID_ROLLBACK_TARGET",
                "The selected rollback version is no longer offered by TrueNAS.");
        }

        var attempt = await CreateAttemptAsync(
            runId,
            app,
            AttemptKind.Rollback,
            targetVersion,
            cancellationToken);
        try
        {
            await SetAppStatusAsync(app.Id, "Updating", "Rollback in progress", cancellationToken);
            var jobId = await trueNasClient.StartRollbackAsync(app.Id, targetVersion, cancellationToken);
            attempt.TrueNasJobId = jobId;
            attempt.Status = AttemptStatus.Running;
            attempt.ReasonCode = "ROLLBACK_STARTED";
            attempt.ReasonMessage = "TrueNAS accepted the rollback job.";
            await SaveAttemptAsync(attempt, cancellationToken);

            var settings = await settingsService.GetRecordAsync(cancellationToken);
            using var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verificationCancellation.CancelAfter(TimeSpan.FromSeconds(settings.VerificationTimeoutSeconds));
            await trueNasClient.WaitForJobAsync(jobId, verificationCancellation.Token);

            attempt.Status = AttemptStatus.Verifying;
            attempt.TrueNasJobState = "SUCCESS";
            await SaveAttemptAsync(attempt, cancellationToken);
            await VerifyAsync(app, AttemptKind.Rollback, targetVersion, verificationCancellation.Token);
            await discoveryService.DiscoverAppAsync(app.Id, cancellationToken);
            await SetSuccessAsync(app.Id, attempt, cancellationToken);
            return new AttemptOutcome(
                attempt.Id,
                AttemptStatus.Succeeded,
                "ROLLBACK_VERIFIED",
                "Rollback verified.",
                jobId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var (code, message) = SanitizeException(exception);
            await SetFailureAsync(app.Id, attempt, code, message, exception, CancellationToken.None);
            return new AttemptOutcome(attempt.Id, AttemptStatus.Failed, code, message, attempt.TrueNasJobId);
        }
    }

    private async Task VerifyAsync(
        AppRecord before,
        AttemptKind kind,
        string? targetVersion,
        CancellationToken cancellationToken)
    {
        var originalImages = DeserializeImages(before.OutdatedImagesJson);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await trueNasClient.GetAppAsync(before.Id, cancellationToken);
            if (current.State is "DEPLOYING" or "STOPPING")
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
                continue;
            }

            if (before.State == "RUNNING" && current.State != "RUNNING")
            {
                throw new TrueNasClientException(
                    "STATE_VERIFICATION_FAILED",
                    $"The app did not return to RUNNING; current state is {current.State}.");
            }

            if (current.State == "CRASHED")
            {
                throw new TrueNasClientException("STATE_VERIFICATION_FAILED", "The app is CRASHED after the operation.");
            }

            if (kind is AttemptKind.CatalogUpgrade or AttemptKind.Rollback &&
                !string.Equals(current.Version, targetVersion, StringComparison.Ordinal))
            {
                throw new TrueNasClientException(
                    "VERSION_VERIFICATION_FAILED",
                    $"TrueNAS reports version {current.Version ?? "unknown"} instead of {targetVersion}.");
            }

            if (kind == AttemptKind.ImageRefresh)
            {
                var remaining = await trueNasClient.GetOutdatedImagesAsync(before.Id, cancellationToken);
                if ((originalImages.Count > 0 && originalImages.Intersect(remaining, StringComparer.Ordinal).Any()) ||
                    (originalImages.Count == 0 && current.ImageUpdatesAvailable))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
                    continue;
                }
            }

            return;
        }
    }

    private async Task<UpdateAttempt> CreateAttemptAsync(
        Guid runId,
        AppRecord app,
        AttemptKind kind,
        string? targetVersion,
        CancellationToken cancellationToken)
    {
        var attempt = new UpdateAttempt
        {
            RunId = runId,
            AppId = app.Id,
            Kind = kind,
            FromVersion = app.InstalledVersion,
            ToVersion = targetVersion,
            OutdatedImagesJson = app.OutdatedImagesJson,
            PolicyAtExecution = app.Policy,
            ScopeAtExecution = app.VersionScope,
            SnapshotRequested = kind == AttemptKind.CatalogUpgrade && app.SnapshotHostPaths,
            StartedUtc = timeProvider.GetUtcNow().UtcDateTime,
            Status = AttemptStatus.Running,
            ReasonCode = "STARTING",
            ReasonMessage = "Preparing the TrueNAS update job."
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    private async Task SaveAttemptAsync(UpdateAttempt attempt, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateAttempts.Update(attempt);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetSuccessAsync(
        string appId,
        UpdateAttempt attempt,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        attempt.Status = AttemptStatus.Succeeded;
        attempt.EndedUtc = now;
        attempt.ReasonCode = attempt.Kind == AttemptKind.Rollback ? "ROLLBACK_VERIFIED" : "VERIFIED";
        attempt.ReasonMessage = attempt.Kind == AttemptKind.Rollback
            ? "The rollback completed and was verified."
            : "The update completed and was verified.";

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateAttempts.Update(attempt);
        var app = await db.Apps.SingleAsync(item => item.Id == appId, cancellationToken);
        app.LastSuccessfulUpdateUtc = now;
        app.StatusLabel = "Up to date";
        app.StatusMessage = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetFailureAsync(
        string appId,
        UpdateAttempt attempt,
        string code,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        attempt.Status = code == "CANCELLED" ? AttemptStatus.Cancelled : AttemptStatus.Failed;
        attempt.EndedUtc = timeProvider.GetUtcNow().UtcDateTime;
        attempt.ReasonCode = code;
        attempt.ReasonMessage = message;
        attempt.TrueNasJobState ??= "FAILED";
        attempt.ErrorDetails = exception is null ? null : exception.GetType().Name;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UpdateAttempts.Update(attempt);
        var app = await db.Apps.SingleAsync(item => item.Id == appId, cancellationToken);
        app.StatusLabel = "Failed";
        app.StatusMessage = message;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetAppStatusAsync(
        string appId,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Apps.SingleAsync(item => item.Id == appId, cancellationToken);
        app.StatusLabel = status;
        app.StatusMessage = message;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<string> DeserializeImages(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (string Code, string Message) SanitizeException(Exception exception) =>
        exception switch
        {
            OperationCanceledException => ("VERIFICATION_TIMEOUT", "Post-update verification timed out."),
            TrueNasClientException trueNas => (trueNas.Code, Truncate(trueNas.Message)),
            _ => ("UPDATE_FAILED", "The update failed; see the TrueNAS job history for details.")
        };

    private static string Truncate(string value) => value.Length <= 1024 ? value : value[..1024];
}
