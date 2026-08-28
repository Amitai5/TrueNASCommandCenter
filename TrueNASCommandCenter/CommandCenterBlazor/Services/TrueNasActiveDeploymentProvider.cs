using System.Text.Json;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

/// <inheritdoc />
public sealed class TrueNasActiveDeploymentProvider(IHttpClientFactory httpClientFactory, TimeProvider timeProvider, ILogger<TrueNasActiveDeploymentProvider> logger) : IActiveDeploymentProvider
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private ActiveDeploymentSnapshot? cached;

    /// <inheritdoc />
    public async Task<ActiveDeploymentSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken = default)
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
                var client = httpClientFactory.CreateClient("truenas-catalog-telemetry");
                var requestUri = new Uri($"https://telemetry.sys.truenas.net/apps/truenas-apps-stats.json?t={now.ToUnixTimeSeconds()}");
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The deployment telemetry response exceeded the 2 MB safety limit.");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The deployment telemetry response exceeded the 2 MB safety limit.");
                }

                var payload = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(bytes) ?? [];
                var counts = new Dictionary<CatalogAppIdentity, long>();
                foreach (var train in payload)
                {
                    foreach (var app in train.Value)
                    {
                        if (TryReadCount(app.Value, out var count) && count >= 0)
                        {
                            counts[Normalize(train.Key, app.Key)] = count;
                        }
                    }
                }

                cached = new ActiveDeploymentSnapshot(counts, now, true);
                return cached;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "The optional TrueNAS deployment telemetry could not be refreshed");
                return cached is null
                    ? new ActiveDeploymentSnapshot(new Dictionary<CatalogAppIdentity, long>(), null, false, Error: "Active deployment data is temporarily unavailable.")
                    : cached with { IsStale = true, Error = "Active deployment data could not be refreshed." };
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Normalizes a train and app name for case-insensitive telemetry lookup.</summary>
    /// <param name="train">The catalog train.</param>
    /// <param name="name">The catalog app name.</param>
    /// <returns>The normalized composite identity.</returns>
    public static CatalogAppIdentity Normalize(string train, string name) => new(train.Trim().ToLowerInvariant(), name.Trim().ToLowerInvariant());

    private static bool IsFresh(ActiveDeploymentSnapshot? snapshot, DateTimeOffset now) =>
        snapshot?.RetrievedAtUtc is not null && now - snapshot.RetrievedAtUtc.Value < CacheDuration;

    private static bool TryReadCount(JsonElement value, out long count)
    {
        count = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out count))
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out count);
    }
}
