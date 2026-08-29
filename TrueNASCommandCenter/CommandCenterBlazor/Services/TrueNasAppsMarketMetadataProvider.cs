using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TrueNasCommandCenter.Services;

/// <inheritdoc />
public sealed class TrueNasAppsMarketMetadataProvider(IHttpClientFactory httpClientFactory, TimeProvider timeProvider, ILogger<TrueNasAppsMarketMetadataProvider> logger) : IAppsMarketMetadataProvider
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly Uri AppsMarketCatalogUri = new("https://apps.truenas.com/catalog/");
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private static readonly Regex AnchorRegex = new("<a\\b(?<attributes>[^>]*)>(?<content>.*?)</a>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex ClassRegex = new("(?:^|\\s)class\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex HrefRegex = new("(?:^|\\s)href\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex AddedDateRegex = new("\\bAdded:\\s*(?<value>\\d{4}-\\d{2}-\\d{2})\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private AppsMarketMetadataSnapshot? cached;

    /// <inheritdoc />
    public async Task<AppsMarketMetadataSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (!forceRefresh && IsFresh(cached, now))
        {
            return cached!;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (!forceRefresh && IsFresh(cached, now))
            {
                return cached!;
            }

            try
            {
                var client = httpClientFactory.CreateClient("truenas-apps-market");
                using var request = new HttpRequestMessage(HttpMethod.Get, AppsMarketCatalogUri);
                request.Headers.Accept.ParseAdd("text/html");
                request.Headers.UserAgent.ParseAdd("TrueNASCommandCenter");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The TrueNAS Apps Market response exceeded the 2 MB safety limit.");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The TrueNAS Apps Market response exceeded the 2 MB safety limit.");
                }

                var dates = Parse(Encoding.UTF8.GetString(bytes));
                if (dates.Count == 0)
                {
                    throw new InvalidDataException("The TrueNAS Apps Market response contained no added-date metadata.");
                }

                cached = new AppsMarketMetadataSnapshot(dates, now, true);
                return cached;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "The optional TrueNAS Apps Market metadata could not be refreshed");
                return cached is null
                    ? new AppsMarketMetadataSnapshot(new Dictionary<string, DateTimeOffset>(), null, false, Error: "Apps Market metadata is temporarily unavailable.")
                    : cached with { IsStale = true, Error = "Apps Market metadata could not be refreshed." };
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> Parse(string html)
    {
        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var attributes = anchor.Groups["attributes"].Value;
            var classMatch = ClassRegex.Match(attributes);
            if (!classMatch.Success || !classMatch.Groups["value"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("catalog-card", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var hrefMatch = HrefRegex.Match(attributes);
            var dateMatch = AddedDateRegex.Match(anchor.Groups["content"].Value);
            if (!hrefMatch.Success || !dateMatch.Success || !TryNormalizeCatalogHref(WebUtility.HtmlDecode(hrefMatch.Groups["value"].Value), out var path))
            {
                continue;
            }

            if (DateTimeOffset.TryParseExact(dateMatch.Groups["value"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateAddedUtc))
            {
                dates[path] = dateAddedUtc;
            }
        }

        return dates;
    }

    private static bool TryNormalizeCatalogHref(string href, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(AppsMarketCatalogUri, href, out var uri))
        {
            return false;
        }

        var normalized = AppsMarketMetadataSnapshot.NormalizeCatalogPath(uri.AbsoluteUri);
        if (normalized is null)
        {
            return false;
        }

        path = normalized;
        return true;
    }

    private static bool IsFresh(AppsMarketMetadataSnapshot? snapshot, DateTimeOffset now) => snapshot?.RetrievedAtUtc is not null && now - snapshot.RetrievedAtUtc.Value < CacheDuration;
}
