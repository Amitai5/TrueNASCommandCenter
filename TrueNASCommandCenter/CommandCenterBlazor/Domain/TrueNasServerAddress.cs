namespace TrueNasCommandCenter.Domain;

/// <summary>Describes the configured TrueNAS hostname, preferred resolved IP address, and safe Web UI origin.</summary>
/// <param name="HostName">The hostname configured by the deployment.</param>
/// <param name="IpAddress">The preferred resolved address, or null when resolution is unavailable.</param>
/// <param name="WebUiUrl">The safe TrueNAS Web UI origin derived from the configured endpoint.</param>
public sealed record TrueNasServerAddress(string HostName, string? IpAddress, string WebUiUrl)
{
    /// <summary>Gets whether an IP address was resolved successfully.</summary>
    public bool IsResolved => !string.IsNullOrWhiteSpace(IpAddress);

    /// <summary>Gets whether the configured hostname adds information beyond the resolved address.</summary>
    public bool HasDistinctHostName => !string.Equals(HostName, IpAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the IP address for display, with an explicit unavailable fallback.</summary>
    public string DisplayAddress => IpAddress ?? "IP unavailable";
}
