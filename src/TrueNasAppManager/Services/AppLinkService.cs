using System.Net;
using System.Net.Sockets;
using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Services;

public interface IAppLinkService
{
    /// <summary>Resolves safe local, remote, and current-route Web UI links for an application.</summary>
    /// <param name="app">The application record including configured links, portals, and ports.</param>
    /// <param name="portalHostOverride">An optional administrator-approved local TrueNAS origin.</param>
    /// <param name="currentManagerUri">The URI currently serving the App Manager page.</param>
    /// <returns>The available Web UI links and route selected for the current page.</returns>
    AppWebUiLinks ResolveWebUiLinks(AppRecord app, string? portalHostOverride, Uri currentManagerUri);

    /// <summary>Classifies a URI as a local or remote route using its host.</summary>
    /// <param name="uri">The absolute HTTP or HTTPS URI to classify.</param>
    /// <returns>The route represented by the URI host.</returns>
    WebUiRoute ClassifyRoute(Uri uri);
}

public sealed class AppLinkService : IAppLinkService
{
    private const string DefaultLocalOrigin = "http://truenas.local";

    /// <inheritdoc cref="IAppLinkService.ResolveWebUiLinks"/>
    public AppWebUiLinks ResolveWebUiLinks(AppRecord app, string? portalHostOverride, Uri currentManagerUri)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(currentManagerUri);

        var selectedRoute = ClassifyRoute(currentManagerUri);
        var localUrl = NormalizeHttpUrl(app.LocalPortalUrl);
        var remoteUrl = NormalizeHttpUrl(app.RemotePortalUrl);
        var legacyUrl = NormalizeHttpUrl(app.ManualPortalUrl);
        if (legacyUrl is not null)
        {
            if (ClassifyRoute(new Uri(legacyUrl)) == WebUiRoute.Local)
            {
                localUrl ??= legacyUrl;
            }
            else
            {
                remoteUrl ??= legacyUrl;
            }
        }

        localUrl ??= ResolveLocalUrl(app, portalHostOverride);
        var selectedUrl = selectedRoute == WebUiRoute.Local ? localUrl : remoteUrl;
        return new AppWebUiLinks(localUrl, remoteUrl, selectedUrl, selectedRoute);
    }

    /// <inheritdoc cref="IAppLinkService.ClassifyRoute"/>
    public WebUiRoute ClassifyRoute(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The Web UI route URI must be absolute.", nameof(uri));
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return WebUiRoute.Local;
        }

        return IPAddress.TryParse(host, out var address) && IsLocalAddress(address) ? WebUiRoute.Local : WebUiRoute.Remote;
    }

    private static string? ResolveLocalUrl(AppRecord app, string? portalHostOverride)
    {
        var portal = app.Portals.Select(item => NormalizeHttpUrl(item.Url)).FirstOrDefault(url => url is not null);
        var approvedHost = NormalizeHttpUrl(portalHostOverride);
        if (portal is not null && approvedHost is not null)
        {
            return RewritePortal(portal, approvedHost);
        }

        if (portal is not null)
        {
            return ShouldUseDefaultLocalHost(portal) ? RewritePortal(portal, DefaultLocalOrigin) : portal;
        }

        var firstPort = app.Ports.Where(item => item.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.HostPort).FirstOrDefault();
        if (firstPort is null)
        {
            return null;
        }

        var localHost = approvedHost ?? DefaultLocalOrigin;
        var hostUri = new Uri(localHost);
        return new UriBuilder(hostUri) { Port = firstPort.HostPort, Path = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private static bool ShouldUseDefaultLocalHost(string portal)
    {
        var host = new Uri(portal).Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
    }

    private static string RewritePortal(string portal, string approvedHost)
    {
        var source = new Uri(portal);
        var target = new Uri(approvedHost);
        return new UriBuilder(source)
        {
            Scheme = target.Scheme,
            Host = target.Host,
            Port = target.IsDefaultPort ? source.Port : target.Port
        }.Uri.AbsoluteUri;
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                bytes[0] == 127 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 169 && bytes[1] == 254;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    private static string? NormalizeHttpUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || !IsSafeHttpUri(uri))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static bool IsSafeHttpUri(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo);
}
