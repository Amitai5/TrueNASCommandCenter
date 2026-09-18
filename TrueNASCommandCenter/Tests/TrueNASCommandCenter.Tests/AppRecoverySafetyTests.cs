using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class AppRecoverySafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 19, 40, 0, TimeSpan.Zero);

    /// <summary>Verifies that a transitional observation cannot rearm automatic recovery.</summary>
    /// <param name="state">The transitional TrueNAS state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow("DEPLOYING")]
    [DataRow("STARTING")]
    [DataRow("STOPPING")]
    [DataRow("UNKNOWN")]
    public async Task EvaluateAsync_TransitionalState_DoesNotResolveOrRearmIncident(string state)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var incidentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await database.ChangeAppAsync(app => { app.HealthIncidentId = incidentId; app.RecoveryAttemptedUtc = Now.UtcDateTime; app.State = state; app.HealthState = AppHealthState.Unknown; });
        var management = new RecordingManagementService();
        var notifications = new NoopNotificationDispatcher();
        var service = new AppHealthMonitorService(database, management, notifications, new FixedTimeProvider(Now));

        await service.EvaluateAsync(["immich"]);
        await database.ChangeAppAsync(app => { app.State = "STOPPED"; app.HealthState = AppHealthState.Stopped; });
        await service.EvaluateAsync(["immich"]);

        Assert.AreEqual(0, management.Calls);
        Assert.IsEmpty(notifications.Events);
        await using var db = database.CreateDbContext();
        var app = await db.Apps.SingleAsync();
        Assert.AreEqual(incidentId, app.HealthIncidentId);
        Assert.AreEqual(Now.UtcDateTime, app.RecoveryAttemptedUtc);
    }

    /// <summary>Verifies that recovery notifications require a subsequent healthy observation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task EvaluateAsync_SuccessfulRestart_WaitsForFreshHealthyInventoryBeforeNotifying()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var management = new RecordingManagementService();
        var notifications = new NoopNotificationDispatcher();
        var service = new AppHealthMonitorService(database, management, notifications, new FixedTimeProvider(Now));

        var first = await service.EvaluateAsync(["immich"]);
        await service.EvaluateAsync(["immich"]);
        await database.ChangeAppAsync(app => { app.State = "RUNNING"; app.HealthState = AppHealthState.Running; });
        await service.EvaluateAsync(["immich"]);

        Assert.AreEqual(1, management.Calls);
        Assert.AreEqual(0, first.Recovered);
        Assert.HasCount(1, notifications.Events, "A lifecycle success and stale inventory must not emit recovery notifications.");
        await database.ChangeAppAsync(app => app.LastHealthCheckUtc = Now.AddSeconds(2).UtcDateTime);
        await service.EvaluateAsync(["immich"]);
        await service.EvaluateAsync(["immich"]);

        CollectionAssert.AreEqual(new[] { NotificationEventType.AppDowntime, NotificationEventType.AppRecoverySucceeded }, notifications.Events.Select(item => item.EventType).ToArray());
        await using var db = database.CreateDbContext();
        var app = await db.Apps.SingleAsync();
        Assert.IsNull(app.HealthIncidentId);
        Assert.IsNull(app.RecoveryAttemptedUtc);
    }

    /// <summary>Verifies that health monitoring does not interrupt unfinished updates.</summary>
    /// <param name="status">The unfinished attempt status.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow(AttemptStatus.Running)]
    [DataRow(AttemptStatus.Verifying)]
    public async Task EvaluateAsync_ActiveUpdate_DoesNotRestartOrNotify(AttemptStatus status)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        await database.AddAttemptAsync(AttemptKind.CatalogUpgrade, status, null, Now.UtcDateTime, 42);
        var management = new RecordingManagementService();
        var notifications = new NoopNotificationDispatcher();
        var service = new AppHealthMonitorService(database, management, notifications, new FixedTimeProvider(Now));

        await service.EvaluateAsync(["immich"]);

        Assert.AreEqual(0, management.Calls);
        Assert.IsEmpty(notifications.Events);
    }

    /// <summary>Verifies the cooldown boundary using persisted history from a previous incident.</summary>
    /// <param name="elapsedSeconds">Time since the previous recovery completed.</param>
    /// <param name="expectedCalls">The expected number of automatic recovery calls.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow(899, 0)]
    [DataRow(900, 1)]
    [DataRow(901, 1)]
    public async Task EvaluateAsync_RecentRecovery_RespectsPersistedCooldown(int elapsedSeconds, int expectedCalls)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        await database.AddAttemptAsync(AttemptKind.AutomaticRecovery, AttemptStatus.Succeeded, Now.AddSeconds(-elapsedSeconds).UtcDateTime, Now.AddSeconds(-elapsedSeconds - 5).UtcDateTime, 42);
        var management = new RecordingManagementService();
        var service = new AppHealthMonitorService(database, management, new NoopNotificationDispatcher(), new FixedTimeProvider(Now));

        await service.EvaluateAsync(["immich"]);

        Assert.AreEqual(expectedCalls, management.Calls);
    }

    /// <summary>Verifies that a deferred preflight does not consume a recovery attempt or duplicate alerts.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task EvaluateAsync_DeferredRecovery_CanRetryWithoutAnotherDowntimeAlert()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var management = new RecordingManagementService { Result = new AppManagementResult(false, "Starting", ErrorCode: "RECOVERY_DEFERRED") };
        var notifications = new NoopNotificationDispatcher();
        var service = new AppHealthMonitorService(database, management, notifications, new FixedTimeProvider(Now));

        await service.EvaluateAsync(["immich"]);
        await database.AddAttemptAsync(AttemptKind.AutomaticRecovery, AttemptStatus.Skipped, Now.UtcDateTime, Now.UtcDateTime, null);
        management.Result = new AppManagementResult(true, "Running", "RUNNING");
        await service.EvaluateAsync(["immich"]);

        Assert.AreEqual(2, management.Calls);
        Assert.HasCount(1, notifications.Events);
        Assert.AreEqual(NotificationEventType.AppDowntime, notifications.Events[0].EventType);
    }

    /// <summary>Verifies that successful job completion alone is not application recovery.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task ExecuteAsync_StartingAfterJobSuccess_WaitsUntilRunning()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var time = new ControlledTimeProvider();
        var client = new LifecycleClient("STOPPED", "DEPLOYING", "RUNNING");
        var service = CreateManagement(database, client, time);

        var operation = service.ExecuteAsync("immich", AppLifecycleAction.Start);
        await time.NextDelayAsync();
        Assert.IsFalse(operation.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        await time.NextDelayAsync();
        Assert.IsFalse(operation.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await operation;

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "start", "wait:42" }, client.Calls);
        await using var db = database.CreateDbContext();
        Assert.AreEqual("RUNNING", (await db.Apps.SingleAsync()).State);
        Assert.AreEqual(AttemptStatus.Succeeded, (await db.UpdateAttempts.SingleAsync()).Status);
    }

    /// <summary>Verifies that lifecycle state verification is bounded without inventing job failure.</summary>
    /// <param name="state">The state that never reaches the expected running state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow("STOPPED")]
    [DataRow("DEPLOYING")]
    [DataRow("CRASHED")]
    public async Task ExecuteAsync_NeverHealthy_ReportsTimeoutWithoutFalseSuccess(string state)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var time = new ControlledTimeProvider();
        var client = new LifecycleClient(state);
        var service = CreateManagement(database, client, time);

        var operation = service.ExecuteAsync("immich", AppLifecycleAction.Start);
        await time.NextDelayAsync();
        time.Advance(TimeSpan.FromSeconds(8));
        var result = await operation;

        Assert.IsFalse(result.Success);
        Assert.AreEqual("LIFECYCLE_VERIFICATION_TIMEOUT", result.ErrorCode);
        await using var db = database.CreateDbContext();
        var attempt = await db.UpdateAttempts.SingleAsync();
        Assert.AreEqual(AttemptStatus.Failed, attempt.Status);
        Assert.AreEqual(42L, attempt.TrueNasJobId);
        Assert.AreEqual("SUCCESS", attempt.TrueNasJobState, "The job succeeded, but application health did not; preserve that distinction.");
    }

    /// <summary>Verifies that unfinished jobs cannot block lifecycle execution indefinitely.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task ExecuteAsync_JobNeverFinishes_TimesOutAndPreservesUnknownCompletion()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var time = new ControlledTimeProvider();
        var client = new LifecycleClient("RUNNING") { HoldJob = true };
        var service = CreateManagement(database, client, time);

        var operation = service.ExecuteAsync("immich", AppLifecycleAction.Start);
        await client.JobEntered.Task;
        time.Advance(TimeSpan.FromSeconds(8));
        var result = await operation;

        Assert.IsFalse(result.Success);
        Assert.AreEqual("LIFECYCLE_VERIFICATION_TIMEOUT", result.ErrorCode);
        Assert.AreEqual(0, client.ReadCount);
        await using var db = database.CreateDbContext();
        Assert.AreEqual("UNKNOWN", (await db.UpdateAttempts.SingleAsync()).TrueNasJobState);
    }

    /// <summary>Verifies that manual lifecycle requests respect the coordinator lock.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task ExecuteAsync_ManualActionDuringUpdate_RejectsOverlappingLifecycleJobs()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var client = new LifecycleClient("RUNNING");
        var runLock = new RunLock();
        await using var lease = await runLock.TryAcquireAsync(CancellationToken.None);
        var service = new AppManagementService(client, database, new ControlledTimeProvider(), NullLogger<AppManagementService>.Instance, runLock);

        var result = await service.ExecuteAsync("immich", AppLifecycleAction.Restart);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("OPERATION_IN_PROGRESS", result.ErrorCode);
        Assert.IsEmpty(client.Calls);
        await using var db = database.CreateDbContext();
        Assert.AreEqual(0, await db.UpdateRuns.CountAsync());
    }

    /// <summary>Verifies that stopped applications are started without an unnecessary stop job.</summary>
    /// <param name="state">The fresh stopped or crashed app state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow("STOPPED")]
    [DataRow("CRASHED")]
    public async Task ExecuteAutomaticRecoveryAsync_StoppedApp_StartsWithoutIssuingStop(string state)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var client = new LifecycleClient(state, "RUNNING");
        var service = CreateManagement(database, client, new ControlledTimeProvider());

        var result = await service.ExecuteAutomaticRecoveryAsync("immich");

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "start", "wait:42" }, client.Calls);
    }

    /// <summary>Verifies that stale inventory does not interrupt an already recovering application.</summary>
    /// <param name="state">The fresh healthy or transitional app state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow("RUNNING")]
    [DataRow("DEPLOYING")]
    [DataRow("STOPPING")]
    [DataRow("UNKNOWN")]
    public async Task ExecuteAutomaticRecoveryAsync_StaleFailure_DoesNotInterruptHealthyOrTransitionalApp(string state)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var client = new LifecycleClient(state);
        var service = CreateManagement(database, client, new ControlledTimeProvider());

        var result = await service.ExecuteAutomaticRecoveryAsync("immich");

        Assert.AreEqual("RECOVERY_DEFERRED", result.ErrorCode);
        Assert.IsEmpty(client.Calls);
        await using var db = database.CreateDbContext();
        Assert.AreEqual(AttemptStatus.Skipped, (await db.UpdateAttempts.SingleAsync()).Status);
    }

    /// <summary>Verifies that a failed workload prevents a healthy lifecycle result.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task ExecuteAsync_RunningWithFailedWorkload_DoesNotReportRecovery()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var time = new ControlledTimeProvider();
        var client = new LifecycleClient("RUNNING") { Workloads = JsonSerializer.SerializeToElement(new { container_details = new[] { new { state = "FAILED" } } }) };
        var service = CreateManagement(database, client, time);

        var operation = service.ExecuteAsync("immich", AppLifecycleAction.Start);
        await time.NextDelayAsync();
        time.Advance(TimeSpan.FromSeconds(8));
        var result = await operation;

        Assert.IsFalse(result.Success);
    }

    /// <summary>Verifies cancellation auditing and release of the shared operation lock.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task ExecuteAsync_CancelledWhileVerifying_PreservesAuditAndReleasesLock()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var time = new ControlledTimeProvider();
        var client = new LifecycleClient("DEPLOYING");
        var runLock = new RunLock();
        var service = new AppManagementService(client, database, time, NullLogger<AppManagementService>.Instance, runLock);
        using var cancellation = new CancellationTokenSource();

        var operation = service.ExecuteAsync("immich", AppLifecycleAction.Start, cancellation.Token);
        await time.NextDelayAsync();
        cancellation.Cancel();
        var result = await operation;

        Assert.AreEqual("CANCELLED", result.ErrorCode);
        Assert.IsFalse(runLock.IsHeld);
        await using var db = database.CreateDbContext();
        Assert.AreEqual(RunStatus.Cancelled, (await db.UpdateRuns.SingleAsync()).Status);
        Assert.AreEqual(AttemptStatus.Cancelled, (await db.UpdateAttempts.SingleAsync()).Status);
    }

    /// <summary>Verifies that a restart cannot start the app before stop-state verification completes.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    public async Task ExecuteAsync_Restart_WaitsForStopBeforeStarting()
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var time = new ControlledTimeProvider();
        var client = new LifecycleClient("STOPPING", "STOPPED", "RUNNING");
        var service = CreateManagement(database, client, time);

        var operation = service.ExecuteAsync("immich", AppLifecycleAction.Restart);
        await time.NextDelayAsync();
        CollectionAssert.AreEqual(new[] { "stop", "wait:43" }, client.Calls);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await operation;

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "stop", "wait:43", "start", "wait:42" }, client.Calls);
    }

    /// <summary>Verifies that a failed stop job prevents the start and retains the TrueNAS failure.</summary>
    /// <param name="code">The job error code.</param>
    /// <param name="expectedState">The persisted terminal job state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow("JOB_FAILED", "FAILED")]
    [DataRow("JOB_ABORTED", "ABORTED")]
    [DataRow("REGISTRY_RATE_LIMIT", "FAILED")]
    public async Task ExecuteAsync_StopJobFails_DoesNotStartAndPreservesFailure(string code, string expectedState)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        var client = new LifecycleClient("STOPPED") { JobError = code };
        var service = CreateManagement(database, client, new ControlledTimeProvider());

        var result = await service.ExecuteAsync("immich", AppLifecycleAction.Restart);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(code, result.ErrorCode);
        CollectionAssert.AreEqual(new[] { "stop", "wait:43" }, client.Calls);
        Assert.AreEqual(0, client.ReadCount);
        await using var db = database.CreateDbContext();
        Assert.AreEqual(expectedState, (await db.UpdateAttempts.SingleAsync()).TrueNasJobState);
    }

    /// <summary>Verifies that recovery rechecks saved operator policy before contacting TrueNAS.</summary>
    /// <param name="action">The current downtime policy.</param>
    /// <param name="maintenance">Whether the app is in maintenance mode.</param>
    /// <param name="installed">Whether the app remains installed.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestMethod]
    [DataRow(DowntimeAction.NotifyOnly, false, true)]
    [DataRow(DowntimeAction.Ignore, false, true)]
    [DataRow(DowntimeAction.RestartAndNotify, true, true)]
    [DataRow(DowntimeAction.RestartAndNotify, false, false)]
    public async Task ExecuteAutomaticRecoveryAsync_PolicyChanged_DoesNotExecute(DowntimeAction action, bool maintenance, bool installed)
    {
        await using var database = await MemoryDatabase.CreateAsync();
        await database.ChangeAppAsync(app => { app.DowntimeAction = action; app.MaintenanceMode = maintenance; app.IsInstalled = installed; });
        var client = new LifecycleClient("STOPPED");
        var service = CreateManagement(database, client, new ControlledTimeProvider());

        var result = await service.ExecuteAutomaticRecoveryAsync("immich");

        Assert.AreEqual("RECOVERY_DEFERRED", result.ErrorCode);
        Assert.IsEmpty(client.Calls);
        Assert.AreEqual(0, client.ReadCount);
        await using var db = database.CreateDbContext();
        Assert.AreEqual(0, await db.UpdateRuns.CountAsync());
    }

    private static AppManagementService CreateManagement(MemoryDatabase database, LifecycleClient client, TimeProvider time) => new(client, database, time, NullLogger<AppManagementService>.Instance, new RunLock());

    private sealed class RecordingManagementService : IAppManagementService
    {
        public int Calls { get; private set; }
        public AppManagementResult Result { get; set; } = new(true, "Running", "RUNNING");
        public Task<AppManagementResult> ExecuteAsync(string appId, AppLifecycleAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppManagementResult> ExecuteAutomaticRecoveryAsync(string appId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class LifecycleClient(params string[] states) : ITrueNasClient
    {
        public bool? HasWriteAccess => true;
        public List<string> Calls { get; } = [];
        public int ReadCount { get; private set; }
        public bool HoldJob { get; init; }
        public string? JobError { get; init; }
        public JsonElement Workloads { get; init; }
        public TaskCompletionSource JobEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(new TrueNasAppDto { Id = "immich", Name = "Immich", State = states[Math.Min(ReadCount++, states.Length - 1)], ActiveWorkloads = Workloads });
        public Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default) { Calls.Add("start"); return Task.FromResult(42L); }
        public Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default) { Calls.Add("stop"); return Task.FromResult(43L); }
        public async Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"wait:{jobId}");
            JobEntered.TrySetResult();
            if (JobError is not null)
            {
                throw new TrueNasClientException(JobError, "The job did not succeed.");
            }

            if (HoldJob)
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }
        }

        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(string appId, string targetVersion = "latest", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartUpgradeAsync(string appId, string targetVersion, bool snapshotHostPaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartRollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetConnectionAsync() => throw new NotSupportedException();
        public Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
            db.Settings.Add(new SettingsRecord { VerificationTimeoutSeconds = 8 });
            db.Apps.Add(new AppRecord { Id = "immich", Name = "Immich", State = "STOPPED", HealthState = AppHealthState.Stopped, DowntimeAction = DowntimeAction.RestartAndNotify, LastHealthCheckUtc = Now.UtcDateTime, LastSeenUtc = Now.UtcDateTime });
            await db.SaveChangesAsync();
            return database;
        }

        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        public async Task ChangeAppAsync(Action<AppRecord> change)
        {
            await using var db = CreateDbContext();
            change(await db.Apps.SingleAsync());
            await db.SaveChangesAsync();
        }

        public async Task AddAttemptAsync(AttemptKind kind, AttemptStatus status, DateTime? ended, DateTime started, long? jobId)
        {
            await using var db = CreateDbContext();
            db.UpdateAttempts.Add(new UpdateAttempt { AppId = "immich", Kind = kind, Status = status, StartedUtc = started, EndedUtc = ended, TrueNasJobId = jobId, Run = new UpdateRun { StartedUtc = started, EndedUtc = ended, Status = ended is null ? RunStatus.Running : RunStatus.Succeeded } });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ControlledTimer> timers = [];
        private readonly Channel<bool> delays = Channel.CreateUnbounded<bool>();
        private DateTimeOffset now = Now;
        public override DateTimeOffset GetUtcNow()
        {
            lock (sync)
            {
                return now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ControlledTimer timer;
            lock (sync)
            {
                timer = new ControlledTimer(this, callback, state, dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + dueTime);
                timers.Add(timer);
            }

            if (dueTime == TimeSpan.FromSeconds(2))
            {
                delays.Writer.TryWrite(true);
            }

            return timer;
        }

        public async Task NextDelayAsync() => await delays.Reader.ReadAsync();
        public void Advance(TimeSpan duration)
        {
            ControlledTimer[] currentTimers;
            DateTimeOffset current;
            lock (sync)
            {
                now += duration;
                current = now;
                currentTimers = timers.ToArray();
            }

            foreach (var timer in currentTimers)
            {
                timer.FireIfDue(current);
            }
        }

        private sealed class ControlledTimer(ControlledTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool disposed;
            private DateTimeOffset dueAt = due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.sync)
                {
                    dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
                    return !disposed;
                }
            }

            public void FireIfDue(DateTimeOffset current)
            {
                lock (owner.sync)
                {
                    if (disposed || current < dueAt)
                    {
                        return;
                    }

                    disposed = true;
                }

                callback(state);
            }

            public void Dispose()
            {
                lock (owner.sync)
                {
                    disposed = true;
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
