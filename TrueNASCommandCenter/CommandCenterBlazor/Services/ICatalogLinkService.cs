namespace TrueNasCommandCenter.Services;

/// <summary>Validates catalog links and selects the official TrueNAS Apps destination.</summary>
public interface ICatalogLinkService
{
    /// <summary>Returns a normalized HTTP or HTTPS URL without embedded credentials.</summary>
    /// <param name="value">The untrusted URL value.</param>
    /// <returns>The normalized safe URL, or <see langword="null"/>.</returns>
    string? NormalizeExternalUrl(string? value);
    /// <summary>Selects an official app page supplied by TrueNAS, falling back to the catalog root.</summary>
    /// <param name="sources">The catalog source links.</param>
    /// <returns>The safe TrueNAS Apps URL.</returns>
    string GetTrueNasAppsUrl(IEnumerable<string> sources);
}
