namespace TrueNasAppManager.Integrations.TrueNas;

/// <summary>Reads optional TrueNAS host, alert, update, storage, and live application resource information.</summary>
public interface ITrueNasSystemClient
{
    /// <summary>Returns basic identity, hardware, and uptime information for the TrueNAS host.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current TrueNAS host information.</returns>
    Task<TrueNasSystemInfoDto> GetSystemInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns all current TrueNAS alerts, including dismissed alerts.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The alerts currently known to TrueNAS.</returns>
    Task<IReadOnlyList<TrueNasAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current TrueNAS operating-system update status.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current operating-system update status.</returns>
    Task<TrueNasUpdateStatusDto> GetUpdateStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current TrueNAS storage pools.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The storage pools visible to the authenticated account.</returns>
    Task<IReadOnlyList<TrueNasPoolDto>> QueryPoolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams current CPU, memory, network, and block-I/O statistics for installed applications.</summary>
    /// <param name="intervalSeconds">The interval between TrueNAS updates, with a minimum of two seconds.</param>
    /// <param name="cancellationToken">A token that stops the stream.</param>
    /// <returns>The asynchronous application statistics stream.</returns>
    IAsyncEnumerable<IReadOnlyList<TrueNasAppStatsDto>> WatchAppStatsAsync(int intervalSeconds = 5, CancellationToken cancellationToken = default);
}
