using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

/// <summary>Builds the read-only host, update, alert, and storage overview for TrueNAS.</summary>
public interface ITrueNasSystemOverviewService
{
    /// <summary>Loads every supported system capability without allowing one optional permission to block the others.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current TrueNAS system overview.</returns>
    Task<TrueNasSystemOverview> GetAsync(CancellationToken cancellationToken = default);
}
