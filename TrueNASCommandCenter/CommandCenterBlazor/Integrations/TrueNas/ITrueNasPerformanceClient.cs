namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Reads realtime and historical performance data from the TrueNAS reporting service.</summary>
public interface ITrueNasPerformanceClient
{
    /// <summary>Returns the reporting graph names and identifiers available on the connected TrueNAS host.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The available Netdata-backed reporting graphs.</returns>
    Task<IReadOnlyList<TrueNasPerformanceGraphDto>> ListPerformanceGraphsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns historical reporting data for the requested graphs and UTC interval.</summary>
    /// <param name="graphs">The reporting graph requests.</param>
    /// <param name="startUtc">The inclusive start of the requested interval.</param>
    /// <param name="endUtc">The inclusive end of the requested interval.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The raw reporting series returned by TrueNAS.</returns>
    Task<IReadOnlyList<TrueNasPerformanceDataDto>> GetPerformanceDataAsync(IReadOnlyList<TrueNasPerformanceGraphRequestDto> graphs, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    /// <summary>Streams current host, interface, disk, ARC, temperature, and pool performance.</summary>
    /// <param name="intervalSeconds">The interval between TrueNAS updates, with a minimum of two seconds.</param>
    /// <param name="cancellationToken">A token that stops the stream.</param>
    /// <returns>The asynchronous realtime performance stream.</returns>
    IAsyncEnumerable<TrueNasRealtimePerformanceDto> WatchSystemPerformanceAsync(int intervalSeconds = 5, CancellationToken cancellationToken = default);
}
