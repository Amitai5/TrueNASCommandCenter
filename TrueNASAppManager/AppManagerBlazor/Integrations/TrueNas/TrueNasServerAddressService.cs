using System.Net;
using System.Net.Sockets;
using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Integrations.TrueNas;

/// <summary>Provides the configured TrueNAS hostname, display-safe resolved IP address, and Web UI origin.</summary>
public interface ITrueNasServerAddressService
{
    /// <summary>Gets the configured TrueNAS hostname, preferred resolved IP address, and Web UI origin.</summary>
    /// <param name="cancellationToken">The token that cancels address resolution.</param>
    /// <returns>The server address safe to display in the UI.</returns>
    Task<TrueNasServerAddress> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Resolves the configured TrueNAS endpoint with a bounded DNS lookup and IPv4 preference.</summary>
public sealed class TrueNasServerAddressService(TrueNasEndpointOptions endpoint, IHostAddressResolver resolver, ILogger<TrueNasServerAddressService> logger) : ITrueNasServerAddressService
{
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(2);
    private TrueNasServerAddress? cachedAddress;

    /// <inheritdoc/>
    public async Task<TrueNasServerAddress> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cachedAddress is { IsResolved: true })
        {
            return cachedAddress;
        }

        var hostName = endpoint.ServerUri.DnsSafeHost;
        var webUiUrl = CreateWebUiUrl(endpoint.ServerUri);
        if (IPAddress.TryParse(hostName, out var literalAddress))
        {
            cachedAddress = new TrueNasServerAddress(hostName, Normalize(literalAddress), webUiUrl);
            return cachedAddress;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolutionTimeout);
        try
        {
            var addresses = await resolver.ResolveAsync(hostName, timeout.Token);
            var preferredAddress = SelectPreferredAddress(addresses);
            var result = new TrueNasServerAddress(hostName, preferredAddress is null ? null : Normalize(preferredAddress), webUiUrl);
            if (result.IsResolved)
            {
                cachedAddress = result;
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Resolving TrueNAS host {HostName} exceeded the {TimeoutSeconds}-second display timeout", hostName, ResolutionTimeout.TotalSeconds);
            return new TrueNasServerAddress(hostName, null, webUiUrl);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            logger.LogWarning(exception, "TrueNAS host {HostName} could not be resolved for display", hostName);
            return new TrueNasServerAddress(hostName, null, webUiUrl);
        }
    }

    private static IPAddress? SelectPreferredAddress(IReadOnlyList<IPAddress> addresses)
    {
        var mappedAddress = addresses.FirstOrDefault(static address => address.IsIPv4MappedToIPv6);
        return addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork) ??
               mappedAddress ??
               addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetworkV6);
    }

    private static string Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    private static string CreateWebUiUrl(Uri endpoint)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, endpoint.DnsSafeHost)
        {
            Path = "/",
            Port = endpoint.IsDefaultPort ? -1 : endpoint.Port
        };
        return builder.Uri.AbsoluteUri;
    }
}
