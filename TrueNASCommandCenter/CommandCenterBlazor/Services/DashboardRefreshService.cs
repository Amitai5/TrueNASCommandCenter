using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.UptimeKuma;

namespace TrueNasCommandCenter.Services;

/// <summary>Refreshes the dashboard's external data sources in their required operational order.</summary>
/// <param name="updateCoordinator">The coordinator that refreshes inventory before evaluating app updates.</param>
/// <param name="uptimeKumaSyncService">The service that imports Uptime Kuma monitor data.</param>
public sealed class DashboardRefreshService(IUpdateCoordinator updateCoordinator, IUptimeKumaSyncService uptimeKumaSyncService)
{
    /// <summary>Refreshes app inventory, evaluates available updates without installing them, and then synchronizes Uptime Kuma.</summary>
    /// <param name="cancellationToken">A token that cancels the refresh.</param>
    /// <returns>The combined update-check and Uptime Kuma results.</returns>
    public async Task<DashboardRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var updateCheck = await updateCoordinator.CheckAndUpdateAsync(RunTrigger.CheckNow, executeUpdates: false, cancellationToken: cancellationToken);
        var uptimeKuma = await uptimeKumaSyncService.SynchronizeAsync(force: true, cancellationToken: cancellationToken);
        return new DashboardRefreshResult(updateCheck, uptimeKuma);
    }
}

/// <summary>Contains the results of one complete dashboard refresh.</summary>
/// <param name="UpdateCheck">The inventory refresh and update evaluation result.</param>
/// <param name="UptimeKuma">The Uptime Kuma synchronization result.</param>
public sealed record DashboardRefreshResult(RunResult UpdateCheck, UptimeKumaSyncResult UptimeKuma)
{
    /// <summary>Gets whether every dashboard source refreshed successfully.</summary>
    public bool IsSuccessful => UpdateCheck.Status == RunStatus.Succeeded && UptimeKuma.Success;

    /// <summary>Gets a user-facing summary of the complete refresh.</summary>
    public string Message => IsSuccessful
        ? $"Dashboard refreshed. App inventory and update check completed. {UptimeKuma.Message}"
        : $"Dashboard refresh completed with issues. App inventory and update check: {UpdateCheck.Message} Uptime Kuma: {UptimeKuma.Message}";
}
