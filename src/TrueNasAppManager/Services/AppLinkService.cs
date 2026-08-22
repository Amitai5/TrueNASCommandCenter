using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Services;

public interface IAppLinkService
{
    /// <summary>Resolves a safe application Web UI URL from explicit TrueNAS and user-provided data.</summary>
    /// <param name="app">The application record including portals and ports.</param>
    /// <param name="portalHostOverride">An optional administrator-approved portal origin.</param>
    /// <returns>The safe Web UI URL, or null when no explicit route is available.</returns>
    string? ResolveWebUiUrl(AppRecord app, string? portalHostOverride);
}

public sealed class AppLinkService : IAppLinkService
{
    /// <inheritdoc cref="IAppLinkService.ResolveWebUiUrl"/>
    public string? ResolveWebUiUrl(AppRecord app, string? portalHostOverride)
    {
        ArgumentNullException.ThrowIfNull(app);
        var manual = NormalizeHttpUrl(app.ManualPortalUrl);
        if (manual is not null)
        {
            return manual;
        }

        var portal = app.Portals.Select(item => NormalizeHttpUrl(item.Url)).FirstOrDefault(url => url is not null);
        var approvedHost = NormalizeHttpUrl(portalHostOverride);
        if (portal is not null && approvedHost is not null)
        {
            var source = new Uri(portal);
            var target = new Uri(approvedHost);
            return new UriBuilder(source) { Scheme = target.Scheme, Host = target.Host, Port = target.IsDefaultPort ? source.Port : target.Port }.Uri.AbsoluteUri;
        }

        if (portal is not null)
        {
            return portal;
        }

        var firstPort = app.Ports.Where(item => item.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.HostPort).FirstOrDefault();
        if (approvedHost is null || firstPort is null)
        {
            return null;
        }

        var approvedUri = new Uri(approvedHost);
        return new UriBuilder(approvedUri) { Port = firstPort.HostPort, Path = string.Empty }.Uri.AbsoluteUri;
    }

    private static string? NormalizeHttpUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }
}
