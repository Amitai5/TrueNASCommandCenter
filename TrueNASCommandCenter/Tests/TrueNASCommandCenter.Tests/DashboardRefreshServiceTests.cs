using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.UptimeKuma;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class DashboardRefreshServiceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task RefreshAsync_AllSourcesSucceed_RunsUpdateCheckBeforeUptimeKuma()
    {
        var callOrder = new List<string>();
        var coordinator = new RecordingUpdateCoordinator(callOrder, new RunResult(Guid.NewGuid(), RunStatus.Succeeded, "Run completed."));
        var uptimeKuma = new RecordingUptimeKumaSyncService(callOrder, new UptimeKumaSyncResult(true, "Imported 14 Uptime Kuma monitors.", 14));
        var service = new DashboardRefreshService(coordinator, uptimeKuma);

        var result = await service.RefreshAsync();

        CollectionAssert.AreEqual(new[] { "update-check", "uptime-kuma" }, callOrder);
        Assert.AreEqual(RunTrigger.CheckNow, coordinator.Trigger);
        Assert.IsFalse(coordinator.ExecuteUpdates);
        Assert.IsTrue(uptimeKuma.Force);
        Assert.IsTrue(result.IsSuccessful);
        StringAssert.Contains(result.Message, "App inventory and update check completed.");
        StringAssert.Contains(result.Message, "Imported 14 Uptime Kuma monitors.");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RefreshAsync_UpdateCheckFails_StillSynchronizesUptimeKumaAndReportsIssue()
    {
        var callOrder = new List<string>();
        var coordinator = new RecordingUpdateCoordinator(callOrder, new RunResult(Guid.NewGuid(), RunStatus.Failed, "TrueNAS was unavailable."));
        var uptimeKuma = new RecordingUptimeKumaSyncService(callOrder, new UptimeKumaSyncResult(true, "Imported cached monitor data.", 4));
        var service = new DashboardRefreshService(coordinator, uptimeKuma);

        var result = await service.RefreshAsync();

        CollectionAssert.AreEqual(new[] { "update-check", "uptime-kuma" }, callOrder);
        Assert.IsFalse(result.IsSuccessful);
        StringAssert.Contains(result.Message, "completed with issues");
        StringAssert.Contains(result.Message, "TrueNAS was unavailable.");
    }

    private sealed class RecordingUpdateCoordinator(List<string> callOrder, RunResult result) : IUpdateCoordinator
    {
        public RunTrigger? Trigger { get; private set; }
        public bool ExecuteUpdates { get; private set; }

        public Task<RunResult> CheckAndUpdateAsync(RunTrigger trigger, bool executeUpdates, string? appId = null, bool riskyStateConfirmed = false, CancellationToken cancellationToken = default)
        {
            callOrder.Add("update-check");
            Trigger = trigger;
            ExecuteUpdates = executeUpdates;
            return Task.FromResult(result);
        }

        public Task<RunResult> RefreshAppsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Dashboard refresh must use the update-check workflow.");
        public Task<RunResult> RunAsync(RunTrigger trigger, bool executeUpdates, string? appId = null, bool riskyStateConfirmed = false, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Dashboard refresh must use CheckAndUpdateAsync.");
        public Task<RunResult> RollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Dashboard refresh must not roll back apps.");
    }

    private sealed class RecordingUptimeKumaSyncService(List<string> callOrder, UptimeKumaSyncResult result) : IUptimeKumaSyncService
    {
        public bool Force { get; private set; }

        public Task<UptimeKumaSyncResult> SynchronizeAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            callOrder.Add("uptime-kuma");
            Force = force;
            return Task.FromResult(result);
        }
    }
}
