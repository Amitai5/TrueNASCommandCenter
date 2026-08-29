namespace TrueNasCommandCenter.Services;

/// <summary>Contains optional app metadata published by the official TrueNAS Apps Market.</summary>
public sealed record AppsMarketMetadataSnapshot(
    IReadOnlyDictionary<string, DateTimeOffset> DateAddedByCatalogPath,
    DateTimeOffset? RetrievedAtUtc,
    bool IsAvailable,
    bool IsStale = false,
    string? Error = null)
{
    /// <summary>Finds the published added date for an official TrueNAS Apps Market URL.</summary>
    /// <param name="catalogUrl">The absolute catalog details URL.</param>
    /// <param name="dateAddedUtc">The published UTC date when found.</param>
    /// <returns><see langword="true"/> when the URL has an added date.</returns>
    public bool TryGetDateAdded(string? catalogUrl, out DateTimeOffset dateAddedUtc)
    {
        dateAddedUtc = default;
        var path = NormalizeCatalogPath(catalogUrl);
        return path is not null && DateAddedByCatalogPath.TryGetValue(path, out dateAddedUtc);
    }

    /// <summary>Normalizes an official Apps Market catalog URL to a stable lookup path.</summary>
    /// <param name="catalogUrl">The absolute catalog URL to normalize.</param>
    /// <returns>The normalized path, or <see langword="null"/> for a non-market URL.</returns>
    public static string? NormalizeCatalogPath(string? catalogUrl)
    {
        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "https" ||
            !uri.Host.Equals("apps.truenas.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
        return path.StartsWith("/catalog/", StringComparison.Ordinal) && path.Length > "/catalog/".Length ? path : null;
    }
}

/// <summary>Provides non-blocking added-date metadata from the official TrueNAS Apps Market.</summary>
public interface IAppsMarketMetadataProvider
{
    /// <summary>Returns cached or freshly downloaded official Apps Market metadata.</summary>
    /// <param name="forceRefresh">Whether to bypass the provider cache.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The optional Apps Market metadata snapshot.</returns>
    Task<AppsMarketMetadataSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken = default);
}
