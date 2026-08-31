namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Reads TrueNAS pool topology, drive identity, temperature, and drive-related alerts.</summary>
public interface ITrueNasDriveHealthClient
{
    /// <summary>Returns storage pools including their topology and current scan state.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The visible storage pools.</returns>
    Task<IReadOnlyList<TrueNasPoolDto>> QueryPoolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns installed disk identity and SMART configuration.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The disks visible to the authenticated account.</returns>
    Task<IReadOnlyList<TrueNasDiskDto>> QueryDisksAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns cached drive temperatures and device-reported thresholds.</summary>
    /// <param name="diskNames">TrueNAS device names such as sda or nvme0n1.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A dictionary of temperature payloads keyed by device name.</returns>
    Task<Dictionary<string, System.Text.Json.JsonElement>> GetDiskTemperaturesAsync(IReadOnlyList<string> diskNames, CancellationToken cancellationToken = default);

    /// <summary>Returns current TrueNAS alerts used to identify SMART and drive warnings.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The alerts currently known to TrueNAS.</returns>
    Task<IReadOnlyList<TrueNasAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default);
}
