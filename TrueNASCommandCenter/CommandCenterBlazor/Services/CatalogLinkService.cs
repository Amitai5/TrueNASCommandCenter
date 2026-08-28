namespace TrueNasCommandCenter.Services;

/// <inheritdoc />
public sealed class CatalogLinkService : ICatalogLinkService
{
    private const string CatalogRoot = "https://apps.truenas.com/catalog/";

    /// <inheritdoc />
    public string? NormalizeExternalUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    /// <inheritdoc />
    public string GetTrueNasAppsUrl(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            var normalized = NormalizeExternalUrl(source);
            if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Host.Equals("apps.truenas.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/catalog/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return CatalogRoot;
    }
}
