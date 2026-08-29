using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

/// <summary>Searches public Docker Hub images and reads metadata needed for TrueNAS custom-app setup.</summary>
public interface IDockerHubDiscoveryService
{
    /// <summary>Searches public Docker Hub images using Docker Hub's native image filters.</summary>
    /// <param name="query">The validated search, filters, sort order, and page.</param>
    /// <param name="forceRefresh">Whether to bypass a recent in-memory result.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A bounded result page and availability state.</returns>
    Task<DockerHubSearchSnapshot> SearchAsync(DockerHubSearchQuery query, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>Gets public repository details and a bounded list of recent tags.</summary>
    /// <param name="identity">The Docker Hub namespace and repository.</param>
    /// <param name="forceRefresh">Whether to bypass a recent in-memory result.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>The repository details and availability state.</returns>
    Task<DockerHubDetailsSnapshot> GetDetailsAsync(DockerHubRepositoryIdentity identity, bool forceRefresh = false, CancellationToken cancellationToken = default);
}
