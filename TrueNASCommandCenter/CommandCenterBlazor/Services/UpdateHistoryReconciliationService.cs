using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

/// <summary>Corrects premature verification failures using subsequently observed application state.</summary>
public sealed class UpdateHistoryReconciliationService(IDbContextFactory<AppDbContext> dbFactory) : IDisposable
{
    /// <summary>Identifies an outcome confirmed by a later inventory observation rather than the original verification.</summary>
    public const string VerifiedAfterRefresh = "VERIFIED_AFTER_REFRESH";
    private readonly SemaphoreSlim reconciliationGate = new(1, 1);

    /// <summary>Repairs completed attempts and run totals when fresh inventory unambiguously confirms their target.</summary>
    /// <param name="cancellationToken">A token that cancels database access.</param>
    /// <returns>The number of corrected attempts; no notifications are sent.</returns>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await reconciliationGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var candidates = await db.UpdateAttempts.Include(attempt => attempt.App).Include(attempt => attempt.Run)
                .Where(attempt => attempt.Status == AttemptStatus.Failed && attempt.Kind == AttemptKind.CatalogUpgrade &&
                    (attempt.ReasonCode == "STATE_VERIFICATION_FAILED" || attempt.ReasonCode == "VERSION_VERIFICATION_FAILED" || attempt.ReasonCode == "VERIFICATION_TIMEOUT") &&
                    attempt.TrueNasJobState == "SUCCESS" && attempt.EndedUtc != null && attempt.Run.EndedUtc != null && attempt.Run.Status != RunStatus.Running)
                .ToListAsync(cancellationToken);
            var changedRunIds = new HashSet<Guid>();
            var changedCount = 0;
            foreach (var attempt in candidates)
            {
                var app = attempt.App;
                if (!app.IsInstalled || !string.Equals(app.State, "RUNNING", StringComparison.OrdinalIgnoreCase) ||
                    app.LastCheckUtc is null || app.LastCheckUtc <= attempt.EndedUtc ||
                    string.IsNullOrWhiteSpace(attempt.ToVersion) || string.Equals(attempt.FromVersion, attempt.ToVersion, StringComparison.Ordinal) ||
                    !string.Equals(app.InstalledVersion, attempt.ToVersion, StringComparison.Ordinal))
                {
                    continue;
                }

                // A retry or rollback may have installed this version instead. Do not
                // rewrite a genuine earlier failure as a successful original attempt.
                var hasLaterOperation = await db.UpdateAttempts.AnyAsync(candidate => candidate.AppId == attempt.AppId && candidate.Id != attempt.Id &&
                    candidate.TrueNasJobId != null && candidate.StartedUtc >= attempt.StartedUtc, cancellationToken);
                if (hasLaterOperation)
                {
                    continue;
                }

                var originalDiagnostic = $"Original verification: {attempt.ReasonCode}: {attempt.ReasonMessage}\n{attempt.ErrorDetails}";
                attempt.ErrorDetails = originalDiagnostic[..Math.Min(originalDiagnostic.Length, 2048)];
                attempt.Status = AttemptStatus.Succeeded;
                attempt.ReasonCode = VerifiedAfterRefresh;
                attempt.ReasonMessage = $"Verified by inventory at {app.LastCheckUtc:O}: the app is RUNNING at requested version {attempt.ToVersion}. The original verification finished before this state was observed.";
                if (app.LastSuccessfulUpdateUtc is null || app.LastSuccessfulUpdateUtc < app.LastCheckUtc)
                {
                    app.LastSuccessfulUpdateUtc = app.LastCheckUtc;
                }

                changedRunIds.Add(attempt.RunId);
                changedCount++;
            }

            foreach (var runId in changedRunIds)
            {
                var run = candidates.First(attempt => attempt.RunId == runId).Run;
                // Tracking returns the corrected attempts above, before the atomic save.
                var attempts = await db.UpdateAttempts.Where(attempt => attempt.RunId == runId).ToListAsync(cancellationToken);
                run.SucceededCount = attempts.Count(attempt => attempt.Status == AttemptStatus.Succeeded);
                run.FailedCount = attempts.Count(attempt => attempt.Status == AttemptStatus.Failed);
                if (run.Status is RunStatus.Failed or RunStatus.PartiallySucceeded && string.IsNullOrWhiteSpace(run.ErrorSummary))
                {
                    run.Status = run.FailedCount == 0 ? RunStatus.Succeeded : run.SucceededCount > 0 ? RunStatus.PartiallySucceeded : RunStatus.Failed;
                }
            }

            if (changedCount > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return changedCount;
        }
        finally
        {
            reconciliationGate.Release();
        }
    }

    /// <summary>Releases the synchronization gate.</summary>
    public void Dispose() => reconciliationGate.Dispose();
}
