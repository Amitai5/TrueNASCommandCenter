using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

public interface IGitHubMetadataService
{
    /// <summary>Refreshes stale metadata for canonical public GitHub sources without failing the parent inventory run.</summary>
    /// <param name="appIds">The applications whose sources should be considered.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task RefreshStaleAsync(IReadOnlyCollection<string> appIds, CancellationToken cancellationToken = default);
    /// <summary>Returns cached GitHub metadata for one application when available.</summary>
    /// <param name="appId">The application identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The cached repository metadata, or null.</returns>
    Task<GitHubRepositoryCache?> GetForAppAsync(string appId, CancellationToken cancellationToken = default);
}

public sealed class GitHubMetadataService(IHttpClientFactory httpClientFactory, IDbContextFactory<AppDbContext> dbFactory, TimeProvider timeProvider, ILogger<GitHubMetadataService> logger) : IGitHubMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    /// <inheritdoc cref="IGitHubMetadataService.RefreshStaleAsync"/>
    public async Task RefreshStaleAsync(IReadOnlyCollection<string> appIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var settings = await db.Settings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
            if (!settings.GitHubEnrichmentEnabled)
            {
                return;
            }

            var sourceSets = await db.Apps.AsNoTracking().Where(app => appIds.Contains(app.Id)).Select(app => app.SourceUrlsJson).ToListAsync(cancellationToken);
            var repositories = sourceSets.SelectMany(DeserializeSources).Select(TryNormalizeRepository).Where(url => url is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await Parallel.ForEachAsync(repositories, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, RefreshRepositorySafeAsync);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "GitHub enrichment stopped without affecting the inventory run");
        }
    }

    /// <inheritdoc cref="IGitHubMetadataService.GetForAppAsync"/>
    public async Task<GitHubRepositoryCache?> GetForAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sources = await db.Apps.AsNoTracking().Where(app => app.Id == appId).Select(app => app.SourceUrlsJson).SingleOrDefaultAsync(cancellationToken);
        var repository = DeserializeSources(sources).Select(TryNormalizeRepository).FirstOrDefault(url => url is not null);
        return repository is null ? null : await db.GitHubRepositories.AsNoTracking().SingleOrDefaultAsync(item => item.RepositoryUrl == repository, cancellationToken);
    }

    private async ValueTask RefreshRepositorySafeAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshRepositoryAsync(repositoryUrl, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "GitHub enrichment failed for {Repository}", repositoryUrl);
            await RecordFailureAsync(repositoryUrl, "GitHub metadata was temporarily unavailable.", CancellationToken.None);
        }
    }

    private async Task RefreshRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cached = await readDb.GitHubRepositories.AsNoTracking().SingleOrDefaultAsync(item => item.RepositoryUrl == repositoryUrl, cancellationToken);
        if (cached?.LastFetchedUtc is not null && now - cached.LastFetchedUtc.Value < CacheDuration)
        {
            return;
        }

        var uri = new Uri(repositoryUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var client = httpClientFactory.CreateClient("github");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(segments[1])}");
        request.Headers.UserAgent.ParseAdd("TrueNASCommandCenter/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(cached?.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            await UpsertAsync(repositoryUrl, record =>
            {
                record.LastFetchedUtc = now;
                record.LastAttemptUtc = now;
                record.LastError = null;
            }, cancellationToken);
            return;
        }

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        await UpsertAsync(repositoryUrl, record =>
        {
            record.ETag = response.Headers.ETag?.Tag;
            record.FullName = ReadString(root, "full_name");
            record.Description = ReadString(root, "description");
            record.License = root.TryGetProperty("license", out var license) ? ReadString(license, "spdx_id") : null;
            record.Stars = root.TryGetProperty("stargazers_count", out var stars) && stars.TryGetInt32(out var count) ? count : null;
            record.TopicsJson = root.TryGetProperty("topics", out var topics) ? topics.GetRawText() : null;
            record.LastFetchedUtc = now;
            record.LastAttemptUtc = now;
            record.LastError = null;
        }, cancellationToken);
    }

    private Task RecordFailureAsync(string repositoryUrl, string error, CancellationToken cancellationToken) => UpsertAsync(repositoryUrl, record =>
    {
        record.LastAttemptUtc = timeProvider.GetUtcNow().UtcDateTime;
        record.LastError = error;
    }, cancellationToken);

    private async Task UpsertAsync(string repositoryUrl, Action<GitHubRepositoryCache> update, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.GitHubRepositories.SingleOrDefaultAsync(item => item.RepositoryUrl == repositoryUrl, cancellationToken);
        if (record is null)
        {
            record = new GitHubRepositoryCache { RepositoryUrl = repositoryUrl };
            db.GitHubRepositories.Add(record);
        }

        update(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<string> DeserializeSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? TryNormalizeRepository(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return $"https://github.com/{segments[0]}/{repository}";
    }

    private static string? ReadString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
