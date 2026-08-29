namespace TrueNasCommandCenter.Domain;

/// <summary>Identifies a public Docker Hub image repository.</summary>
public sealed record DockerHubRepositoryIdentity(string Namespace, string Repository)
{
    /// <summary>Gets the fully qualified repository name used by Docker Hub APIs.</summary>
    public string QualifiedName => $"{Namespace}/{Repository}";

    /// <summary>Gets the image name most users should enter in a TrueNAS custom app.</summary>
    public string DisplayName => Namespace.Equals("library", StringComparison.OrdinalIgnoreCase) ? Repository : QualifiedName;
}

/// <summary>Classifies Docker Hub trusted-content badges.</summary>
public enum DockerHubBadge
{
    None,
    Official,
    VerifiedPublisher,
    OpenSource
}

/// <summary>Filters Docker Hub search results by trusted-content program.</summary>
public enum DockerHubTrustFilter
{
    All,
    Official,
    VerifiedPublisher,
    OpenSource
}

/// <summary>Defines the Docker Hub search sort options exposed by its native search API.</summary>
public enum DockerHubSortOrder
{
    BestMatch,
    PullCount,
    RecentlyUpdated
}

/// <summary>Classifies Docker Hub request availability for actionable UI states.</summary>
public enum DockerHubAvailability
{
    Available,
    RateLimited,
    Offline,
    NotFound,
    Failed
}

/// <summary>Describes one native Docker Hub filter option.</summary>
public sealed record DockerHubFilterOption(string Value, string Label);

/// <summary>Provides the image filter values supported by Docker Hub search.</summary>
public static class DockerHubNativeFilters
{
    /// <summary>Gets the category slugs accepted by Docker Hub image search.</summary>
    public static IReadOnlyList<DockerHubFilterOption> Categories { get; } = Array.AsReadOnly<DockerHubFilterOption>(
    [
        new("api-management", "API management"),
        new("content-management-system", "Content management system"),
        new("data-science", "Data science"),
        new("developer-tools", "Developer tools"),
        new("databases-storage", "Databases & storage"),
        new("languages-frameworks", "Languages & frameworks"),
        new("integration-delivery", "Integration & delivery"),
        new("internet-of-things", "Internet of things"),
        new("machine-learning-ai", "Machine learning & AI"),
        new("message-queues", "Message queues"),
        new("monitoring-observability", "Monitoring & observability"),
        new("networking", "Networking"),
        new("operating-systems", "Operating systems"),
        new("security", "Security"),
        new("web-servers", "Web servers"),
        new("web-analytics", "Web analytics")
    ]);

    /// <summary>Gets the operating-system values accepted by Docker Hub image search.</summary>
    public static IReadOnlyList<DockerHubFilterOption> OperatingSystems { get; } = Array.AsReadOnly<DockerHubFilterOption>(
    [
        new("linux", "Linux"),
        new("windows", "Windows")
    ]);

    /// <summary>Gets the CPU architecture values accepted by Docker Hub image search.</summary>
    public static IReadOnlyList<DockerHubFilterOption> Architectures { get; } = Array.AsReadOnly<DockerHubFilterOption>(
    [
        new("amd64", "x86-64"),
        new("386", "x86"),
        new("arm64", "ARM 64"),
        new("arm", "ARM"),
        new("ppc64", "IBM POWER"),
        new("ppc64le", "PowerPC 64 LE"),
        new("s390x", "IBM Z")
    ]);
}

/// <summary>Defines one bounded Docker Hub image search request.</summary>
public sealed record DockerHubSearchQuery(
    string Search,
    DockerHubTrustFilter Trust,
    string Category,
    string OperatingSystem,
    string Architecture,
    DockerHubSortOrder SortOrder,
    int Page = 1,
    int PageSize = 24);

/// <summary>Represents a Docker Hub image result suitable for a TrueNAS custom app.</summary>
public sealed record DockerHubRepositorySummary(
    DockerHubRepositoryIdentity Identity,
    string Description,
    DockerHubBadge Badge,
    string Publisher,
    long StarCount,
    long? PullCount,
    string PullCountText,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> OperatingSystems,
    IReadOnlyList<string> Architectures,
    bool IsArchived,
    string? LogoUrl,
    string DockerHubUrl);

/// <summary>Contains one page of Docker Hub image results and its request status.</summary>
public sealed record DockerHubSearchSnapshot(
    IReadOnlyList<DockerHubRepositorySummary> Repositories,
    long Total,
    int Page,
    int PageSize,
    DateTimeOffset? RetrievedAtUtc,
    DockerHubAvailability Availability,
    bool IsStale = false,
    string? Message = null)
{
    /// <summary>Gets whether another result page is available.</summary>
    public bool HasNextPage => (long)Page * PageSize < Total;

    /// <summary>Gets whether a previous result page is available.</summary>
    public bool HasPreviousPage => Page > 1;
}

/// <summary>Describes one operating-system and CPU pairing published for a Docker image tag.</summary>
public sealed record DockerHubPlatform(string OperatingSystem, string Architecture, string? Variant)
{
    /// <summary>Gets a compact platform label.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Variant) ? $"{OperatingSystem}/{Architecture}" : $"{OperatingSystem}/{Architecture}/{Variant}";
}

/// <summary>Describes one public Docker Hub tag and its published platforms.</summary>
public sealed record DockerHubTag(
    string Name,
    string? Digest,
    long? SizeBytes,
    DateTimeOffset? LastPushedUtc,
    IReadOnlyList<DockerHubPlatform> Platforms);

/// <summary>Represents Docker Hub repository details and a bounded list of recent tags.</summary>
public sealed record DockerHubRepositoryDetails(
    DockerHubRepositoryIdentity Identity,
    string Description,
    string ReadmeText,
    DockerHubBadge Badge,
    string Publisher,
    long StarCount,
    long? PullCount,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string Status,
    bool IsAutomated,
    bool IsPrivate,
    bool IsArchived,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> OperatingSystems,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> ContentTypes,
    IReadOnlyList<DockerHubTag> Tags,
    long TotalTags,
    string? LogoUrl,
    string DockerHubUrl)
{
    /// <summary>Gets the preferred initial tag for a custom-app image reference.</summary>
    public string PreferredTag => Tags.FirstOrDefault(tag => tag.Name.Equals("latest", StringComparison.OrdinalIgnoreCase))?.Name ?? Tags.FirstOrDefault()?.Name ?? "latest";

    /// <summary>Builds a Docker Hub image reference for the supplied tag.</summary>
    /// <param name="tag">The selected Docker Hub tag.</param>
    /// <returns>A qualified image reference accepted by TrueNAS custom apps.</returns>
    public string GetImageReference(string tag) => $"docker.io/{Identity.QualifiedName}:{tag}";
}

/// <summary>Contains Docker Hub details and its request status.</summary>
public sealed record DockerHubDetailsSnapshot(
    DockerHubRepositoryDetails? Repository,
    DateTimeOffset? RetrievedAtUtc,
    DockerHubAvailability Availability,
    bool IsStale = false,
    string? Message = null);
