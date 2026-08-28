using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

/// <summary>Loads a display-safe storage pool overview without making optional pool access block the app dashboard.</summary>
public interface IStoragePoolOverviewService
{
    /// <summary>Loads storage capacity and health visible to the authenticated TrueNAS account.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current pool overview or an explicit optional-permission state.</returns>
    Task<StoragePoolOverview> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Maps TrueNAS pool data into the compact dashboard model.</summary>
public sealed class StoragePoolOverviewService(ITrueNasSystemClient trueNasClient, ILogger<StoragePoolOverviewService> logger) : IStoragePoolOverviewService
{
    /// <inheritdoc />
    public async Task<StoragePoolOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pools = await trueNasClient.QueryPoolsAsync(cancellationToken);
            return new StoragePoolOverview(pools
                .OrderBy(pool => pool.Name, StringComparer.OrdinalIgnoreCase)
                .Select(pool => new StoragePoolHealth(
                    pool.Name,
                    pool.Status,
                    pool.Healthy,
                    pool.Warning,
                    pool.StatusDetail,
                    pool.Size,
                    pool.Allocated,
                    pool.Free,
                    pool.Fragmentation))
                .ToList());
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("Storage pool overview is unavailable because the TrueNAS account does not have POOL_READ");
            return new StoragePoolOverview([], RequiresPoolRead: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Storage pool overview could not be loaded");
            return new StoragePoolOverview([], Error: "Storage pool health is temporarily unavailable.");
        }
    }

    private static bool IsPermissionFailure(TrueNasClientException exception) =>
        exception.Code is "-32001" or "EACCES" ||
        exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("authorized", StringComparison.OrdinalIgnoreCase);
}
