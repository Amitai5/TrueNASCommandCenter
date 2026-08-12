using TrueNasUpdateManager.Domain;

namespace TrueNasUpdateManager.Integrations.TrueNas;

public interface ITrueNasClient
{
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default);
    Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default);
    Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(
        string appId,
        string targetVersion = "latest",
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default);
    Task<long> StartUpgradeAsync(
        string appId,
        string targetVersion,
        bool snapshotHostPaths,
        CancellationToken cancellationToken = default);
    Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default);
    Task<long> StartRollbackAsync(
        string appId,
        string targetVersion,
        CancellationToken cancellationToken = default);
    Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default);
    Task ResetConnectionAsync();
}

public sealed class TrueNasClientException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
