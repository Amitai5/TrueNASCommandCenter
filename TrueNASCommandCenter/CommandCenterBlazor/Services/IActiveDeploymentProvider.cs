using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

/// <summary>Contains optional deployment counts from the public TrueNAS telemetry dataset.</summary>
public sealed record ActiveDeploymentSnapshot(
    IReadOnlyDictionary<CatalogAppIdentity, long> Counts,
    DateTimeOffset? RetrievedAtUtc,
    bool IsAvailable,
    bool IsStale = false,
    string? Error = null);

/// <summary>Provides non-blocking, read-only deployment counts for catalog apps.</summary>
public interface IActiveDeploymentProvider
{
    /// <summary>Returns cached or freshly downloaded public deployment counts.</summary>
    /// <param name="forceRefresh">Whether to bypass the provider cache.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The optional deployment count snapshot.</returns>
    Task<ActiveDeploymentSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken = default);
}
