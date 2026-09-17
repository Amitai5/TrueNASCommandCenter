using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class UpdateHistoryReconciliationServiceTests
{
    private static readonly DateTime StartedUtc = new(2026, 9, 14, 6, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow("STATE_VERIFICATION_FAILED")]
    [DataRow("VERSION_VERIFICATION_FAILED")]
    [DataRow("VERIFICATION_TIMEOUT")]
    public async Task ReconcileAsync_FreshRunningTarget_RepairsAttemptAndRunWithoutLosingOriginalEvidence(string reason)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var attempt = CreateAttempt();
        attempt.ReasonCode = reason;
        await SeedAsync(database, attempt);
        using var service = new UpdateHistoryReconciliationService(database);

        var changed = await service.ReconcileAsync();
        var repeated = await service.ReconcileAsync();

        Assert.AreEqual(1, changed);
        Assert.AreEqual(0, repeated);
        await using var db = database.CreateDbContext();
        var repaired = await db.UpdateAttempts.Include(item => item.Run).Include(item => item.App).SingleAsync();
        Assert.AreEqual(AttemptStatus.Succeeded, repaired.Status);
        Assert.AreEqual(UpdateHistoryReconciliationService.VerifiedAfterRefresh, repaired.ReasonCode);
        StringAssert.Contains(repaired.ReasonMessage, "requested version 2.0.21");
        StringAssert.Contains(repaired.ErrorDetails!, reason);
        StringAssert.Contains(repaired.ErrorDetails!, "current state is STOPPED");
        StringAssert.Contains(repaired.ErrorDetails!, "original diagnostic");
        Assert.AreEqual(StartedUtc.AddSeconds(2), repaired.EndedUtc, "Keep the timestamp of the original verification attempt.");
        Assert.AreEqual(StartedUtc.AddMinutes(1), repaired.App.LastSuccessfulUpdateUtc);
        Assert.AreEqual(RunStatus.Succeeded, repaired.Run.Status);
        Assert.AreEqual(1, repaired.Run.SucceededCount);
        Assert.AreEqual(0, repaired.Run.FailedCount);
        Assert.AreEqual(11, repaired.Run.CheckedCount);
        Assert.AreEqual(0, await db.Notifications.CountAsync(), "Repairing historical evidence must not resend notifications.");
    }

    [TestMethod]
    [DataRow("stopped")]
    [DataRow("crashed")]
    [DataRow("deploying")]
    [DataRow("missing-app")]
    [DataRow("stale-inventory")]
    [DataRow("same-instant")]
    [DataRow("no-inventory-time")]
    [DataRow("wrong-version")]
    [DataRow("newer-version")]
    [DataRow("no-target")]
    [DataRow("unchanged-version")]
    [DataRow("failed-job")]
    [DataRow("unknown-job")]
    [DataRow("real-failure")]
    [DataRow("image-refresh")]
    [DataRow("rollback")]
    [DataRow("unfinished-attempt")]
    [DataRow("unfinished-run")]
    public async Task ReconcileAsync_InsufficientEvidence_PreservesFailure(string scenario)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var attempt = CreateAttempt();
        switch (scenario)
        {
            case "stopped": attempt.App.State = "STOPPED"; break;
            case "crashed": attempt.App.State = "CRASHED"; break;
            case "deploying": attempt.App.State = "DEPLOYING"; break;
            case "missing-app": attempt.App.IsInstalled = false; break;
            case "stale-inventory": attempt.App.LastCheckUtc = StartedUtc; break;
            case "same-instant": attempt.App.LastCheckUtc = attempt.EndedUtc; break;
            case "no-inventory-time": attempt.App.LastCheckUtc = null; break;
            case "wrong-version": attempt.App.InstalledVersion = "2.0.20"; break;
            case "newer-version": attempt.App.InstalledVersion = "2.0.22"; break;
            case "no-target": attempt.ToVersion = null; break;
            case "unchanged-version": attempt.FromVersion = attempt.ToVersion; break;
            case "failed-job": attempt.TrueNasJobState = "FAILED"; break;
            case "unknown-job": attempt.TrueNasJobState = null; break;
            case "real-failure": attempt.ReasonCode = "JOB_FAILED"; break;
            case "image-refresh": attempt.Kind = AttemptKind.ImageRefresh; break;
            case "rollback": attempt.Kind = AttemptKind.Rollback; break;
            case "unfinished-attempt": attempt.EndedUtc = null; break;
            case "unfinished-run": attempt.Run.Status = RunStatus.Running; attempt.Run.EndedUtc = null; break;
            default: Assert.Fail($"Unknown scenario: {scenario}"); break;
        }

        await SeedAsync(database, attempt);
        using var service = new UpdateHistoryReconciliationService(database);

        Assert.AreEqual(0, await service.ReconcileAsync());
        await using var db = database.CreateDbContext();
        var unchanged = await db.UpdateAttempts.SingleAsync();
        Assert.AreEqual(AttemptStatus.Failed, unchanged.Status);
        Assert.AreEqual(attempt.ReasonCode, unchanged.ReasonCode);
        Assert.AreEqual("original diagnostic", unchanged.ErrorDetails);
    }

    [TestMethod]
    [DataRow(AttemptKind.CatalogUpgrade, AttemptStatus.Succeeded)]
    [DataRow(AttemptKind.CatalogUpgrade, AttemptStatus.Failed)]
    [DataRow(AttemptKind.Rollback, AttemptStatus.Succeeded)]
    public async Task ReconcileAsync_LaterOperationCouldHaveInstalledTarget_DoesNotRewriteEarlierFailure(AttemptKind kind, AttemptStatus status)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var original = CreateAttempt();
        await SeedAsync(database, original);
        await using (var db = database.CreateDbContext())
        {
            db.UpdateAttempts.Add(new UpdateAttempt
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Run = new UpdateRun { Id = Guid.Parse("00000000-0000-0000-0000-000000000012"), StartedUtc = StartedUtc.AddSeconds(10), EndedUtc = StartedUtc.AddSeconds(20), Status = RunStatus.Succeeded },
                AppId = original.AppId,
                Kind = kind,
                Status = status,
                ToVersion = "2.0.21",
                StartedUtc = StartedUtc.AddSeconds(10),
                TrueNasJobId = 10365
            });
            await db.SaveChangesAsync();
        }

        using var service = new UpdateHistoryReconciliationService(database);

        Assert.AreEqual(0, await service.ReconcileAsync());
        await using var verified = database.CreateDbContext();
        Assert.AreEqual(AttemptStatus.Failed, (await verified.UpdateAttempts.SingleAsync(item => item.Id == original.Id)).Status);
    }

    [TestMethod]
    public async Task ReconcileAsync_MixedRun_RecountsOutcomesWithoutClearingUnrelatedFailuresOrSkips()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var attempt = CreateAttempt();
        attempt.Run.FailedCount = 2;
        attempt.Run.SkippedCount = 1;
        await SeedAsync(database, attempt);
        await using (var db = database.CreateDbContext())
        {
            db.Apps.Add(new AppRecord { Id = "plex", Name = "Plex" });
            db.UpdateAttempts.AddRange(
                new UpdateAttempt { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), RunId = attempt.RunId, AppId = "plex", Status = AttemptStatus.Failed, ReasonCode = "JOB_FAILED", StartedUtc = StartedUtc },
                new UpdateAttempt { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), RunId = attempt.RunId, AppId = "plex", Status = AttemptStatus.Blocked, ReasonCode = "ACTION_REQUIRED", StartedUtc = StartedUtc });
            await db.SaveChangesAsync();
        }

        using var service = new UpdateHistoryReconciliationService(database);
        await service.ReconcileAsync();

        await using var verified = database.CreateDbContext();
        var run = await verified.UpdateRuns.SingleAsync();
        Assert.AreEqual(RunStatus.PartiallySucceeded, run.Status);
        Assert.AreEqual(1, run.SucceededCount);
        Assert.AreEqual(1, run.FailedCount);
        Assert.AreEqual(1, run.SkippedCount);
    }

    [TestMethod]
    [DataRow(RunStatus.Cancelled, null)]
    [DataRow(RunStatus.Failed, "Inventory refresh failed after updating.")]
    public async Task ReconcileAsync_RunHasIndependentFailure_PreservesRunStatus(RunStatus status, string? error)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var attempt = CreateAttempt();
        attempt.Run.Status = status;
        attempt.Run.ErrorSummary = error;
        await SeedAsync(database, attempt);
        using var service = new UpdateHistoryReconciliationService(database);

        Assert.AreEqual(1, await service.ReconcileAsync());

        await using var db = database.CreateDbContext();
        var run = await db.UpdateRuns.SingleAsync();
        Assert.AreEqual(status, run.Status);
        Assert.AreEqual(error, run.ErrorSummary);
        Assert.AreEqual(1, run.SucceededCount);
    }

    private static UpdateAttempt CreateAttempt() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        RunId = Guid.Parse("00000000-0000-0000-0000-000000000011"),
        Run = new UpdateRun
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000011"),
            Trigger = RunTrigger.Scheduled,
            StartedUtc = StartedUtc,
            EndedUtc = StartedUtc.AddSeconds(3),
            Status = RunStatus.Failed,
            CheckedCount = 11,
            EligibleCount = 1,
            FailedCount = 1
        },
        AppId = "cloudflared",
        App = new AppRecord { Id = "cloudflared", Name = "cloudflared", IsInstalled = true, State = "RUNNING", InstalledVersion = "2.0.21", LastCheckUtc = StartedUtc.AddMinutes(1) },
        Kind = AttemptKind.CatalogUpgrade,
        FromVersion = "2.0.20",
        ToVersion = "2.0.21",
        StartedUtc = StartedUtc,
        EndedUtc = StartedUtc.AddSeconds(2),
        Status = AttemptStatus.Failed,
        ReasonCode = "STATE_VERIFICATION_FAILED",
        ReasonMessage = "The app did not return to RUNNING; current state is STOPPED.",
        TrueNasJobId = 10364,
        TrueNasJobState = "SUCCESS",
        ErrorDetails = "original diagnostic"
    };

    private static async Task SeedAsync(MemoryDatabase database, UpdateAttempt attempt)
    {
        await using var db = database.CreateDbContext();
        db.UpdateAttempts.Add(attempt);
        await db.SaveChangesAsync();
    }

    private sealed class MemoryDatabase : IDbContextFactory<AppDbContext>, IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        public static async Task<MemoryDatabase> CreateAsync()
        {
            var database = new MemoryDatabase();
            await database.connection.OpenAsync();
            await using var db = database.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return database;
        }

        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
