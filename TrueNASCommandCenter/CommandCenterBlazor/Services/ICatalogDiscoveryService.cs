using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

/// <summary>Builds cached, read-only gallery and details views from the TrueNAS catalog.</summary>
public interface ICatalogDiscoveryService
{
    /// <summary>Returns the current gallery snapshot, optionally refreshing TrueNAS and telemetry sources.</summary>
    /// <param name="forceRefresh">Whether to bypass local and TrueNAS catalog caches.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The catalog gallery snapshot.</returns>
    Task<CatalogDiscoverySnapshot> GetCatalogAsync(bool forceRefresh, CancellationToken cancellationToken = default);
    /// <summary>Returns one catalog app and optional similar-app suggestions.</summary>
    /// <param name="identity">The app train and catalog name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The catalog details snapshot.</returns>
    Task<CatalogDetailsSnapshot> GetDetailsAsync(CatalogAppIdentity identity, CancellationToken cancellationToken = default);
}
