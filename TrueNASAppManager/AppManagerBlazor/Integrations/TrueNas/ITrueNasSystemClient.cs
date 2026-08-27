namespace TrueNasAppManager.Integrations.TrueNas;

/// <summary>Reads optional TrueNAS storage information and live application resource statistics.</summary>
public interface ITrueNasSystemClient
{
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
