using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Integrations.TrueNas;

public interface ITrueNasClient
{
    bool? HasWriteAccess { get; }
    /// <summary>Tests authentication and reports the TrueNAS roles available to the configured service account.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The connection and role test result.</returns>
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns all installed TrueNAS applications and their current workload metadata.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The installed applications.</returns>
    Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns one installed TrueNAS application.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The matching application.</returns>
    Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Returns outdated container images for an application.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The outdated image references.</returns>
    Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Returns the available catalog upgrade summary for an application.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="targetVersion">The requested target version.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The catalog upgrade summary.</returns>
    Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(string appId, string targetVersion = "latest", CancellationToken cancellationToken = default);
    /// <summary>Returns available rollback versions for an application.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The available rollback versions.</returns>
    Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Starts an installed TrueNAS app.</summary>
    /// <param name="appId">The TrueNAS app identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The TrueNAS job identifier.</returns>
    Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Stops a running TrueNAS app.</summary>
    /// <param name="appId">The TrueNAS app identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The TrueNAS job identifier.</returns>
    Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Starts a catalog application upgrade.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="targetVersion">The target catalog version.</param>
    /// <param name="snapshotHostPaths">Whether TrueNAS should snapshot eligible host paths.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The TrueNAS job identifier.</returns>
    Task<long> StartUpgradeAsync(string appId, string targetVersion, bool snapshotHostPaths, CancellationToken cancellationToken = default);
    /// <summary>Pulls current images and redeploys a custom application.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The TrueNAS job identifier.</returns>
    Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Rolls a catalog application back to an available version.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="targetVersion">The rollback version.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The TrueNAS job identifier.</returns>
    Task<long> StartRollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default);
    /// <summary>Waits for a TrueNAS job to finish and throws when the job fails.</summary>
    /// <param name="jobId">The TrueNAS job identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default);
    /// <summary>Sends an email using the TrueNAS mail service.</summary>
    /// <param name="message">The message and optional explicit recipients.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default);
    /// <summary>Streams recent and live logs for one TrueNAS application container.</summary>
    /// <param name="request">The application, container, and tail size.</param>
    /// <param name="cancellationToken">A token that stops the stream.</param>
    /// <returns>The asynchronous log stream.</returns>
    IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, CancellationToken cancellationToken = default);
    /// <summary>Closes the current connection so the next operation reconnects with current settings.</summary>
    Task ResetConnectionAsync();
}

public sealed class TrueNasClientException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
