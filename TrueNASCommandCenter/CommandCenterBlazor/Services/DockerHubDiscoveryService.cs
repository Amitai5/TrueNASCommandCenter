using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.DockerHub;

namespace TrueNasCommandCenter.Services;

/// <inheritdoc />
public sealed class DockerHubDiscoveryService(IHttpClientFactory httpClientFactory, ICatalogReadmeSanitizer readmeSanitizer, TimeProvider timeProvider, ILogger<DockerHubDiscoveryService> logger) : IDockerHubDiscoveryService
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumSearchLength = 100;
    private const int MaximumPageSize = 48;
    private const int DetailsTagCount = 24;
    private const string TrustedBadges = "official,verified_publisher";
    private const string TargetOperatingSystem = "linux";
    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Regex RepositorySegmentRegex = new("^[a-z0-9](?:[a-z0-9._-]{0,253}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, DockerHubSearchSnapshot> searchCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DockerHubDetailsSnapshot> detailsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DockerHubSearchSnapshot> SearchAsync(DockerHubSearchQuery query, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = Normalize(query);
        var cacheKey = CreateSearchPath(normalized);
        var now = timeProvider.GetUtcNow();
        if (!forceRefresh && searchCache.TryGetValue(cacheKey, out var existing) && IsFresh(existing.RetrievedAtUtc, SearchCacheDuration, now))
        {
            return existing;
        }

        try
        {
            var response = await GetJsonAsync<DockerHubSearchResponse>(cacheKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                throw new InvalidDataException($"Docker Hub search returned an error: {response.Error}");
            }

            var repositories = (response.Results ?? [])
                .Where(static item => item.Type?.Equals("image", StringComparison.OrdinalIgnoreCase) is not false)
                .Where(IsTrustedLinuxImage)
                .Select(MapSummary)
                .Where(static item => item is not null)
                .Cast<DockerHubRepositorySummary>()
                .ToList();
            var snapshot = new DockerHubSearchSnapshot(repositories, response.Total ?? repositories.Count, normalized.Page, normalized.PageSize, now, DockerHubAvailability.Available);
            searchCache[cacheKey] = snapshot;
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SearchFailure(cacheKey, normalized, now, exception);
        }
    }

    /// <inheritdoc />
    public async Task<DockerHubDetailsSnapshot> GetDetailsAsync(DockerHubRepositoryIdentity identity, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var normalized = Normalize(identity);
        var cacheKey = normalized.QualifiedName;
        var now = timeProvider.GetUtcNow();
        if (!forceRefresh && detailsCache.TryGetValue(cacheKey, out var existing) && IsFresh(existing.RetrievedAtUtc, DetailsCacheDuration, now))
        {
            return existing;
        }

        try
        {
            var encodedNamespace = Uri.EscapeDataString(normalized.Namespace);
            var encodedRepository = Uri.EscapeDataString(normalized.Repository);
            var detailsTask = GetJsonAsync<DockerHubRepositoryDto>($"v2/namespaces/{encodedNamespace}/repositories/{encodedRepository}", cancellationToken);
            var tagsTask = GetJsonAsync<DockerHubTagPageDto>($"v2/namespaces/{encodedNamespace}/repositories/{encodedRepository}/tags?page=1&page_size={DetailsTagCount}", cancellationToken);
            var latestTagTask = TryGetTagAsync(normalized, "latest", cancellationToken);
            var metadataTask = TryGetSearchMetadataAsync(normalized, cancellationToken);
            await Task.WhenAll(detailsTask, tagsTask, latestTagTask, metadataTask);

            var repository = MapDetails(normalized, await detailsTask, await tagsTask, await latestTagTask, await metadataTask);
            var snapshot = new DockerHubDetailsSnapshot(repository, now, DockerHubAvailability.Available);
            detailsCache[cacheKey] = snapshot;
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DetailsFailure(cacheKey, now, exception);
        }
    }

    /// <summary>Builds the bounded Docker Hub search path for a validated query.</summary>
    /// <param name="query">The normalized search query.</param>
    /// <returns>The relative Docker Hub search path.</returns>
    public static string CreateSearchPath(DockerHubSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = Normalize(query);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("custom_boosted_results", "true"),
            new("type", "image"),
            new("badges", TrustedBadges),
            new("operating_systems", TargetOperatingSystem),
            new("from", ((normalized.Page - 1) * normalized.PageSize).ToString(CultureInfo.InvariantCulture)),
            new("size", normalized.PageSize.ToString(CultureInfo.InvariantCulture))
        };

        AddOptional(parameters, "query", normalized.Search);
        AddOptional(parameters, "categories", normalized.Category);
        AddOptional(parameters, "architectures", normalized.Architecture);
        if (normalized.SortOrder is DockerHubSortOrder.PullCount)
        {
            parameters.Add(new("sort", "pull_count"));
            parameters.Add(new("order", "desc"));
        }
        else if (normalized.SortOrder is DockerHubSortOrder.RecentlyUpdated)
        {
            parameters.Add(new("sort", "updated_at"));
            parameters.Add(new("order", "desc"));
        }

        return "api/search/v4?" + string.Join('&', parameters.Select(static parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private async Task<DockerHubSearchRepositoryDto?> TryGetSearchMetadataAsync(DockerHubRepositoryIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            var query = new DockerHubSearchQuery(identity.DisplayName, string.Empty, string.Empty, DockerHubSortOrder.BestMatch, 1, 12);
            var response = await GetJsonAsync<DockerHubSearchResponse>(CreateSearchPath(query), cancellationToken);
            return response.Results?.FirstOrDefault(item => TryGetIdentity(item, out var candidate) && candidate == identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Docker Hub search metadata was unavailable for {DockerHubRepository}", identity.QualifiedName);
            return null;
        }
    }

    private async Task<DockerHubTagDto?> TryGetTagAsync(DockerHubRepositoryIdentity identity, string tag, CancellationToken cancellationToken)
    {
        try
        {
            var encodedNamespace = Uri.EscapeDataString(identity.Namespace);
            var encodedRepository = Uri.EscapeDataString(identity.Repository);
            var encodedTag = Uri.EscapeDataString(tag);
            return await GetJsonAsync<DockerHubTagDto>($"v2/namespaces/{encodedNamespace}/repositories/{encodedRepository}/tags/{encodedTag}", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerHubNotFoundException)
        {
            return null;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Docker Hub tag metadata was unavailable for {DockerHubRepository}:{DockerHubTag}", identity.QualifiedName, tag);
            return null;
        }
    }

    private async Task<T> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("docker-hub");
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("TrueNASCommandCenter");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw new DockerHubRateLimitException(response.Headers.RetryAfter?.ToString());
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new DockerHubNotFoundException();
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The Docker Hub response exceeded the 4 MB safety limit.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("The Docker Hub response exceeded the 4 MB safety limit.");
        }

        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Docker Hub returned an empty or invalid response.");
    }

    private DockerHubSearchSnapshot SearchFailure(string cacheKey, DockerHubSearchQuery query, DateTimeOffset now, Exception exception)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        logger.LogWarning(exception, "Docker Hub image search failed. DiagnosticId={DiagnosticId}", diagnosticId);
        var availability = Classify(exception);
        var message = FailureMessage(availability, diagnosticId);
        return searchCache.TryGetValue(cacheKey, out var cached)
            ? cached with { IsStale = true, Message = $"{message} Showing the last successful results." }
            : new DockerHubSearchSnapshot([], 0, query.Page, query.PageSize, now, availability, Message: message);
    }

    private DockerHubDetailsSnapshot DetailsFailure(string cacheKey, DateTimeOffset now, Exception exception)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        logger.LogWarning(exception, "Docker Hub repository details failed for {DockerHubRepository}. DiagnosticId={DiagnosticId}", cacheKey, diagnosticId);
        var availability = Classify(exception);
        var message = FailureMessage(availability, diagnosticId);
        return detailsCache.TryGetValue(cacheKey, out var cached)
            ? cached with { IsStale = true, Message = $"{message} Showing the last successful details." }
            : new DockerHubDetailsSnapshot(null, now, availability, Message: message);
    }

    private DockerHubRepositoryDetails MapDetails(DockerHubRepositoryIdentity identity, DockerHubRepositoryDto details, DockerHubTagPageDto tags, DockerHubTagDto? latestTag, DockerHubSearchRepositoryDto? metadata)
    {
        var tagDtos = (tags.Results ?? []).Where(static tag => !string.IsNullOrWhiteSpace(tag.Name)).ToList();
        if (!string.IsNullOrWhiteSpace(latestTag?.Name) && !tagDtos.Any(tag => tag.Name!.Equals(latestTag.Name, StringComparison.OrdinalIgnoreCase)))
        {
            tagDtos.Insert(0, latestTag);
        }

        var mappedTags = tagDtos
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Name))
            .Select(static tag => new DockerHubTag(tag.Name!.Trim(), NormalizeDigest(tag.Digest), tag.FullSize, tag.TagLastPushed ?? tag.LastUpdated, MapPlatforms(tag.Images)))
            .ToList();
        return new DockerHubRepositoryDetails(
            identity,
            FirstText(details.Description, metadata?.ShortDescription, "No description was published."),
            readmeSanitizer.Sanitize(details.FullDescription),
            MapBadge(metadata?.Badge),
            FirstText(metadata?.Publisher?.Name, details.Namespace, identity.Namespace),
            Math.Max(details.StarCount, metadata?.StarCount ?? 0),
            details.PullCount ?? metadata?.RawPullCount,
            details.DateRegistered ?? metadata?.CreatedAt,
            details.LastUpdated ?? metadata?.UpdatedAt,
            FirstText(details.StatusDescription, "Unknown"),
            details.IsAutomated,
            details.IsPrivate,
            metadata?.Archived ?? false,
            NamedValues(metadata?.Categories?.Select(static item => item.Name), details.Categories?.Select(static item => item.Name)),
            NamedValues(metadata?.OperatingSystems?.Select(static item => item.Label)),
            NamedValues(metadata?.Architectures?.Select(static item => item.Label)),
            NamedValues(details.MediaTypes, metadata?.MediaTypes),
            NamedValues(details.ContentTypes, metadata?.ContentTypes),
            mappedTags,
            tags.Count,
            NormalizeLogoUrl(metadata?.LogoUrl?.Large ?? metadata?.LogoUrl?.Small),
            CreateDockerHubUrl(identity));
    }

    private static DockerHubRepositorySummary? MapSummary(DockerHubSearchRepositoryDto source)
    {
        if (!TryGetIdentity(source, out var identity))
        {
            return null;
        }

        return new DockerHubRepositorySummary(
            identity,
            FirstText(source.ShortDescription, "No description was published."),
            MapBadge(source.Badge),
            FirstText(source.Publisher?.Name, identity.Namespace),
            Math.Max(0, source.StarCount),
            source.RawPullCount,
            FirstText(source.PullCount, source.RawPullCount?.ToString("N0", CultureInfo.InvariantCulture), "Unavailable"),
            source.CreatedAt,
            source.UpdatedAt,
            NamedValues(source.Categories?.Select(static item => item.Name)),
            NamedValues(source.OperatingSystems?.Select(static item => item.Label)),
            NamedValues(source.Architectures?.Select(static item => item.Label)),
            source.Archived,
            NormalizeLogoUrl(source.LogoUrl?.Large ?? source.LogoUrl?.Small),
            CreateDockerHubUrl(identity));
    }

    private static IReadOnlyList<DockerHubPlatform> MapPlatforms(IReadOnlyList<DockerHubTagImageDto>? images) => (images ?? [])
        .Where(static image => !string.IsNullOrWhiteSpace(image.OperatingSystem) && !string.IsNullOrWhiteSpace(image.Architecture))
        .Where(static image => !image.OperatingSystem!.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !image.Architecture!.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        .Select(static image => new DockerHubPlatform(image.OperatingSystem!.Trim(), image.Architecture!.Trim(), string.IsNullOrWhiteSpace(image.Variant) ? null : image.Variant.Trim()))
        .Distinct()
        .OrderBy(static platform => platform.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static DockerHubSearchQuery Normalize(DockerHubSearchQuery query)
    {
        var search = query.Search?.Trim() ?? string.Empty;
        if (search.Length > MaximumSearchLength)
        {
            throw new ArgumentException($"Docker Hub searches cannot exceed {MaximumSearchLength} characters.", nameof(query));
        }

        return query with
        {
            Search = search,
            Category = NormalizeFilter(query.Category, DockerHubNativeFilters.Categories, nameof(query.Category)),
            Architecture = NormalizeFilter(query.Architecture, DockerHubNativeFilters.Architectures, nameof(query.Architecture)),
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize)
        };
    }

    private static DockerHubRepositoryIdentity Normalize(DockerHubRepositoryIdentity identity)
    {
        var namespaceValue = identity.Namespace?.Trim().ToLowerInvariant() ?? string.Empty;
        var repository = identity.Repository?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RepositorySegmentRegex.IsMatch(namespaceValue) || !RepositorySegmentRegex.IsMatch(repository))
        {
            throw new ArgumentException("Docker Hub namespace and repository names contain unsupported characters.", nameof(identity));
        }

        return new DockerHubRepositoryIdentity(namespaceValue, repository);
    }

    private static string NormalizeFilter(string? value, IReadOnlyList<DockerHubFilterOption> options, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (!options.Any(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The Docker Hub filter value is not supported.", parameterName);
        }

        return normalized;
    }

    private static bool TryGetIdentity(DockerHubSearchRepositoryDto source, out DockerHubRepositoryIdentity identity)
    {
        identity = new DockerHubRepositoryIdentity(string.Empty, string.Empty);
        var idParts = source.Id?.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var namespaceValue = idParts is { Length: 2 } ? idParts[0] : source.Publisher?.Name;
        var repository = idParts is { Length: 2 } ? idParts[1] : source.Name;
        if (string.IsNullOrWhiteSpace(namespaceValue) || string.IsNullOrWhiteSpace(repository) || !RepositorySegmentRegex.IsMatch(namespaceValue) || !RepositorySegmentRegex.IsMatch(repository))
        {
            return false;
        }

        identity = new DockerHubRepositoryIdentity(namespaceValue.ToLowerInvariant(), repository.ToLowerInvariant());
        return true;
    }

    private static bool IsTrustedLinuxImage(DockerHubSearchRepositoryDto source)
    {
        var badge = source.Badge?.Trim();
        var isTrusted = badge?.Equals("official", StringComparison.OrdinalIgnoreCase) is true || badge?.Equals("verified_publisher", StringComparison.OrdinalIgnoreCase) is true;
        var isLinux = source.OperatingSystems?.Any(static operatingSystem =>
            operatingSystem.Name?.Equals(TargetOperatingSystem, StringComparison.OrdinalIgnoreCase) is true ||
            operatingSystem.Label?.Equals("Linux", StringComparison.OrdinalIgnoreCase) is true) is true;
        return isTrusted && isLinux;
    }

    private static DockerHubBadge MapBadge(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "official" => DockerHubBadge.Official,
        "verified_publisher" => DockerHubBadge.VerifiedPublisher,
        "open_source" => DockerHubBadge.OpenSource,
        _ => DockerHubBadge.None
    };

    private static DockerHubAvailability Classify(Exception exception) => exception switch
    {
        DockerHubRateLimitException => DockerHubAvailability.RateLimited,
        DockerHubNotFoundException => DockerHubAvailability.NotFound,
        HttpRequestException or TaskCanceledException => DockerHubAvailability.Offline,
        _ => DockerHubAvailability.Failed
    };

    private static string FailureMessage(DockerHubAvailability availability, string diagnosticId) => availability switch
    {
        DockerHubAvailability.RateLimited => $"Docker Hub is rate limiting public searches. Wait a moment and retry. Diagnostic ID: {diagnosticId}.",
        DockerHubAvailability.NotFound => $"That public Docker Hub repository was not found. Diagnostic ID: {diagnosticId}.",
        DockerHubAvailability.Offline => $"Docker Hub could not be reached from this container. Diagnostic ID: {diagnosticId}.",
        _ => $"Docker Hub returned an unexpected response. Diagnostic ID: {diagnosticId}."
    };

    private static IReadOnlyList<string> NamedValues(params IEnumerable<string?>?[] sources) => sources
        .Where(static source => source is not null)
        .SelectMany(static source => source!)
        .Where(static value => !string.IsNullOrWhiteSpace(value) && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        .Select(static value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string FirstText(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? NormalizeLogoUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not "https" || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return uri.Host.Equals("djeqr6to3dedg.cloudfront.net", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.gravatar.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static string CreateDockerHubUrl(DockerHubRepositoryIdentity identity) => identity.Namespace.Equals("library", StringComparison.OrdinalIgnoreCase)
        ? $"https://hub.docker.com/_/{Uri.EscapeDataString(identity.Repository)}"
        : $"https://hub.docker.com/r/{Uri.EscapeDataString(identity.Namespace)}/{Uri.EscapeDataString(identity.Repository)}";

    private static string? NormalizeDigest(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddOptional(ICollection<KeyValuePair<string, string>> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add(new(name, value));
        }
    }

    private static bool IsFresh(DateTimeOffset? retrievedAtUtc, TimeSpan duration, DateTimeOffset now) => retrievedAtUtc is not null && now - retrievedAtUtc.Value < duration;

    private sealed class DockerHubRateLimitException(string? retryAfter) : Exception($"Docker Hub rate limit exceeded. RetryAfter={retryAfter ?? "unspecified"}");

    private sealed class DockerHubNotFoundException : Exception
    {
    }
}
