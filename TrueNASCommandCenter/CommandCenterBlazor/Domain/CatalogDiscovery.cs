namespace TrueNasCommandCenter.Domain;

/// <summary>Identifies a catalog app without merging duplicate names from different trains.</summary>
public sealed record CatalogAppIdentity(string Train, string Name);

/// <summary>Describes a Linux capability requested by a catalog app.</summary>
public sealed record CatalogCapability(string Name, string Description);

/// <summary>Describes one user and group context published for a catalog workload.</summary>
public sealed record CatalogRunAsContext(int? UserId, string? UserName, int? GroupId, string? GroupName, string Description);

/// <summary>Describes a catalog maintainer and an optional safe website.</summary>
public sealed record CatalogMaintainer(string Name, string Email, string? Url);

/// <summary>Represents a read-only app returned by the TrueNAS catalog.</summary>
public sealed record CatalogApp
{
    public required CatalogAppIdentity Identity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? LatestVersion { get; init; }
    public string? LatestAppVersion { get; init; }
    public string? LatestHumanVersion { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; init; }
    public DateTimeOffset? DateAddedUtc { get; init; }
    public int? PopularityRank { get; init; }
    public bool IsRecommended { get; init; }
    public bool? IsInstalled { get; init; }
    public bool IsCatalogHealthy { get; init; }
    public string? CatalogHealthError { get; init; }
    public long? ActiveDeployments { get; init; }
    public DateTimeOffset? ActiveDeploymentsRetrievedAtUtc { get; init; }
    public bool IsActiveDeploymentDataStale { get; init; }
    public string? IconUrl { get; init; }
    public string? HomeUrl { get; init; }
    public required string TrueNasAppsUrl { get; init; }
    public IReadOnlyList<string> SourceUrls { get; init; } = [];
    public IReadOnlyList<string> ScreenshotUrls { get; init; } = [];
    public IReadOnlyList<CatalogMaintainer> Maintainers { get; init; } = [];
    public IReadOnlyList<CatalogCapability> Capabilities { get; init; } = [];
    public IReadOnlyList<CatalogRunAsContext> RunAsContexts { get; init; } = [];
    public IReadOnlyList<string> RequiredFeatures { get; init; } = [];
    public IReadOnlyList<string> HostMounts { get; init; } = [];
    public string? MinimumTrueNasVersion { get; init; }
    public string ReadmeText { get; init; } = string.Empty;
}

/// <summary>Classifies an unavailable catalog request for an actionable page state.</summary>
public enum CatalogAvailability
{
    Available,
    PermissionDenied,
    Offline,
    Failed
}

/// <summary>Contains a catalog result, including a retained stale snapshot after refresh failure.</summary>
public sealed record CatalogDiscoverySnapshot(
    IReadOnlyList<CatalogApp> Apps,
    DateTimeOffset? RefreshedAtUtc,
    CatalogAvailability Availability,
    bool IsStale = false,
    string? Message = null,
    bool HasPopularityRanks = false,
    bool HasDateAdded = false,
    DateTimeOffset? DeploymentDataAtUtc = null,
    bool IsDeploymentDataAvailable = false,
    bool IsDeploymentDataStale = false);

/// <summary>Contains details and similar-app results for one catalog identity.</summary>
public sealed record CatalogDetailsSnapshot(
    CatalogApp? App,
    IReadOnlyList<CatalogApp> SimilarApps,
    CatalogAvailability Availability,
    string? Message = null);

/// <summary>Filters installed or recommended states without conflating unknown values.</summary>
public enum CatalogPresenceFilter
{
    All,
    Yes,
    No
}

/// <summary>Defines the supported catalog gallery sort orders.</summary>
public enum CatalogSortOrder
{
    Name,
    Newest,
    RecentlyUpdated,
    Popularity,
    ActiveDeployments
}

/// <summary>Defines the current search, filter, and sort selection for the gallery.</summary>
public sealed record CatalogGalleryQuery(
    string Search,
    string Category,
    string Train,
    CatalogPresenceFilter Installed,
    CatalogPresenceFilter Recommended,
    CatalogSortOrder SortOrder);

/// <summary>Applies deterministic catalog search, filter, and sort rules.</summary>
public static class CatalogGalleryQueryEngine
{
    /// <summary>Returns catalog apps matching the supplied gallery query.</summary>
    /// <param name="apps">The catalog apps to query.</param>
    /// <param name="query">The search, filter, and sort selection.</param>
    /// <returns>The matching apps in the requested order.</returns>
    public static IReadOnlyList<CatalogApp> Apply(IEnumerable<CatalogApp> apps, CatalogGalleryQuery query)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(query);

        var result = apps.Where(app => MatchesSearch(app, query.Search))
            .Where(app => MatchesValue(app.Categories, query.Category))
            .Where(app => string.IsNullOrWhiteSpace(query.Train) || app.Identity.Train.Equals(query.Train, StringComparison.OrdinalIgnoreCase))
            .Where(app => MatchesPresence(app.IsInstalled, query.Installed))
            .Where(app => MatchesPresence(app.IsRecommended, query.Recommended));

        return query.SortOrder switch
        {
            CatalogSortOrder.Newest => result.OrderBy(app => app.DateAddedUtc is null).ThenByDescending(app => app.DateAddedUtc).ThenBy(app => app.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            CatalogSortOrder.RecentlyUpdated => result.OrderBy(app => app.LastUpdatedUtc is null).ThenByDescending(app => app.LastUpdatedUtc).ThenBy(app => app.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            CatalogSortOrder.Popularity => result.OrderBy(app => app.PopularityRank is null).ThenBy(app => app.PopularityRank).ThenBy(app => app.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            CatalogSortOrder.ActiveDeployments => result.OrderBy(app => app.ActiveDeployments is null).ThenByDescending(app => app.ActiveDeployments).ThenBy(app => app.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => result.OrderBy(app => app.Title, StringComparer.OrdinalIgnoreCase).ThenBy(app => app.Identity.Train, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static bool MatchesSearch(CatalogApp app, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();
        return app.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            app.Identity.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            app.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            app.Identity.Train.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            app.Categories.Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            app.Tags.Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesValue(IEnumerable<string> values, string filter) =>
        string.IsNullOrWhiteSpace(filter) || values.Any(value => value.Equals(filter, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesPresence(bool? value, CatalogPresenceFilter filter) => filter switch
    {
        CatalogPresenceFilter.Yes => value is true,
        CatalogPresenceFilter.No => value is false,
        _ => true
    };
}
