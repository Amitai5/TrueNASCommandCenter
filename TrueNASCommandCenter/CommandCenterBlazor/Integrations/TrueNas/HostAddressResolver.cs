using System.Net;

namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Resolves hostnames at the operating-system network boundary.</summary>
public interface IHostAddressResolver
{
    /// <summary>Resolves every IP address currently associated with a hostname.</summary>
    /// <param name="hostName">The DNS hostname to resolve.</param>
    /// <param name="cancellationToken">The token that cancels resolution.</param>
    /// <returns>The resolved IP addresses.</returns>
    Task<IPAddress[]> ResolveAsync(string hostName, CancellationToken cancellationToken = default);
}

/// <summary>Resolves hostnames through the system DNS configuration.</summary>
public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    /// <inheritdoc/>
    public Task<IPAddress[]> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
        Dns.GetHostAddressesAsync(hostName, cancellationToken);
}
