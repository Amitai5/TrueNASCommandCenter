using System.Globalization;
using System.Text.Json;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

/// <inheritdoc />
public sealed class CatalogDiscoveryService(
    ITrueNasCatalogClient catalogClient,
    ITrueNasClient trueNasClient,
    IActiveDeploymentProvider deploymentProvider,
    ICatalogLinkService linkService,
    ICatalogReadmeSanitizer readmeSanitizer,
    TimeProvider timeProvider,
    ILogger<CatalogDiscoveryService> logger) : ICatalogDiscoveryService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CatalogDiscoverySnapshot? cached;

    /// <inheritdoc />
    public async Task<CatalogDiscoverySnapshot> GetCatalogAsync(bool forceRefresh, CancellationToken cancellationToken = default)
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
                var catalog = await catalogClient.QueryCatalogAppsAsync(forceRefresh, cancellationToken);
                var identities = catalog.SelectMany(train => train.Value.Keys.Select(name => new CatalogAppIdentity(train.Key, name))).ToList();
                var installed = await QueryInstalledAsync(identities, cancellationToken);
                var deployments = await deploymentProvider.GetAsync(forceRefresh, cancellationToken);
                var apps = Flatten(catalog, installed, deployments);
                cached = new CatalogDiscoverySnapshot(
                    apps,
                    now,
                    CatalogAvailability.Available,
                    Message: installed.IsAvailable ? null : "Installed-app matching is temporarily unavailable.",
                    HasPopularityRanks: apps.Any(app => app.PopularityRank is not null),
                    HasDateAdded: apps.Any(app => app.DateAddedUtc is not null),
                    DeploymentDataAtUtc: deployments.RetrievedAtUtc,
                    IsDeploymentDataAvailable: deployments.IsAvailable,
                    IsDeploymentDataStale: deployments.IsStale);
                return cached;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var diagnosticId = Guid.NewGuid().ToString("N");
                var (availability, message) = Classify(exception, diagnosticId);
                logger.LogWarning(exception, "The TrueNAS app catalog could not be refreshed. Availability={CatalogAvailability} DiagnosticId={DiagnosticId}", availability, diagnosticId);
                if (cached is not null && cached.Apps.Count > 0)
                {
                    cached = cached with
                    {
                        IsStale = true,
                        Message = $"Catalog refresh failed. Showing the last successful results. {message}"
                    };
                    return cached;
                }

                return new CatalogDiscoverySnapshot([], null, availability, Message: message);
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CatalogDiscoverySnapshot> ReconnectAndRefreshAsync(CancellationToken cancellationToken = default)
    {
        await trueNasClient.ResetConnectionAsync();
        return await GetCatalogAsync(true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CatalogDetailsSnapshot> GetDetailsAsync(CatalogAppIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Train) || string.IsNullOrWhiteSpace(identity.Name))
        {
            return new CatalogDetailsSnapshot(null, [], CatalogAvailability.Failed, "The catalog app identity is invalid.");
        }

        var gallery = await GetCatalogAsync(false, cancellationToken);
        var summary = Find(gallery.Apps, identity);
        if (summary is null && gallery.Availability is not CatalogAvailability.Available)
        {
            return new CatalogDetailsSnapshot(null, [], gallery.Availability, gallery.Message);
        }

        try
        {
            var dto = await catalogClient.GetCatalogAppDetailsAsync(identity.Name, identity.Train, cancellationToken);
            var app = Map(identity.Train, identity.Name, dto, summary?.IsInstalled, summary?.ActiveDeployments, summary?.ActiveDeploymentsRetrievedAtUtc, summary?.IsActiveDeploymentDataStale ?? false);
            IReadOnlyList<CatalogApp> similar = [];
            try
            {
                var similarDtos = await catalogClient.QuerySimilarCatalogAppsAsync(identity.Name, identity.Train, cancellationToken);
                similar = similarDtos
                    .Select(item =>
                    {
                        var name = string.IsNullOrWhiteSpace(item.Name) ? item.Title : item.Name;
                        var matchingSummary = Find(gallery.Apps, new CatalogAppIdentity(identity.Train, name));
                        return Map(identity.Train, name, item, matchingSummary?.IsInstalled, matchingSummary?.ActiveDeployments, matchingSummary?.ActiveDeploymentsRetrievedAtUtc, matchingSummary?.IsActiveDeploymentDataStale ?? false);
                    })
                    .Where(item => !IdentityEquals(item.Identity, identity))
                    .DistinctBy(item => Normalize(item.Identity))
                    .Take(6)
                    .ToList();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Similar apps were unavailable for {CatalogTrain}/{CatalogAppName}", identity.Train, identity.Name);
            }

            return new CatalogDetailsSnapshot(app, similar, CatalogAvailability.Available, gallery.IsStale ? gallery.Message : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var diagnosticId = Guid.NewGuid().ToString("N");
            var (availability, message) = Classify(exception, diagnosticId);
            logger.LogWarning(exception, "Catalog details were unavailable for {CatalogTrain}/{CatalogAppName}. DiagnosticId={DiagnosticId}", identity.Train, identity.Name, diagnosticId);
            return summary is null
                ? new CatalogDetailsSnapshot(null, [], availability, message)
                : new CatalogDetailsSnapshot(summary, [], CatalogAvailability.Available, $"Detailed catalog data could not be refreshed. {message}");
        }
    }

    private IReadOnlyList<CatalogApp> Flatten(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>> catalog,
        InstalledMatchSnapshot installed,
        ActiveDeploymentSnapshot deployments)
    {
        var result = new List<CatalogApp>();
        foreach (var train in catalog.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var app in train.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var identity = Normalize(new CatalogAppIdentity(train.Key, app.Key));
                bool? isInstalled = installed.IsAvailable ? installed.Identities.Contains(identity) : null;
                var deploymentIdentity = TrueNasActiveDeploymentProvider.Normalize(train.Key, app.Key);
                deployments.Counts.TryGetValue(deploymentIdentity, out var count);
                result.Add(Map(train.Key, app.Key, app.Value, isInstalled, deployments.Counts.ContainsKey(deploymentIdentity) ? count : null, deployments.RetrievedAtUtc, deployments.IsStale));
            }
        }

        return result;
    }

    private CatalogApp Map(string train, string catalogName, TrueNasCatalogAppDto source, bool? isInstalled, long? activeDeployments, DateTimeOffset? deploymentRetrievedAtUtc = null, bool isDeploymentDataStale = false)
    {
        var name = string.IsNullOrWhiteSpace(source.Name) ? catalogName : source.Name;
        var safeSources = source.Sources.Select(linkService.NormalizeExternalUrl).Where(value => value is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var screenshots = source.Screenshots.Select(linkService.NormalizeExternalUrl).Where(value => value is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new CatalogApp
        {
            Identity = new CatalogAppIdentity(train, name),
            Title = string.IsNullOrWhiteSpace(source.Title) ? name : source.Title,
            Description = source.Description,
            Categories = CleanValues(source.Categories),
            Tags = CleanValues(source.Tags),
            LatestVersion = source.LatestVersion,
            LatestAppVersion = source.LatestAppVersion,
            LatestHumanVersion = source.LatestHumanVersion,
            LastUpdatedUtc = ParseDate(source.LastUpdate),
            DateAddedUtc = ParseDate(ReadAdditionalString(source, "date_added")),
            PopularityRank = ReadAdditionalInt(source, "popularity_rank"),
            IsRecommended = source.Recommended,
            IsInstalled = isInstalled,
            IsCatalogHealthy = source.Healthy,
            CatalogHealthError = source.HealthyError,
            ActiveDeployments = activeDeployments,
            ActiveDeploymentsRetrievedAtUtc = deploymentRetrievedAtUtc,
            IsActiveDeploymentDataStale = isDeploymentDataStale,
            IconUrl = linkService.NormalizeExternalUrl(source.IconUrl),
            HomeUrl = linkService.NormalizeExternalUrl(source.Home),
            TrueNasAppsUrl = linkService.GetTrueNasAppsUrl(source.Sources),
            SourceUrls = safeSources,
            ScreenshotUrls = screenshots,
            Maintainers = source.Maintainers.Select(maintainer => new CatalogMaintainer(maintainer.Name, maintainer.Email, linkService.NormalizeExternalUrl(maintainer.Url))).ToList(),
            Capabilities = source.Capabilities.Select(capability => new CatalogCapability(capability.Name, capability.Description)).ToList(),
            RunAsContexts = source.RunAsContext.Select(context => new CatalogRunAsContext(context.UserId, context.UserName, context.GroupId, context.GroupName, context.Description)).ToList(),
            RequiredFeatures = ReadAdditionalStringList(source, "required_features"),
            HostMounts = ReadAdditionalStringList(source, "host_mounts"),
            MinimumTrueNasVersion = ReadAdditionalString(source, "min_scale_version") ?? ReadNestedAdditionalString(source, "annotations", "min_scale_version"),
            ReadmeText = readmeSanitizer.Sanitize(source.AppReadme)
        };
    }

    private async Task<InstalledMatchSnapshot> QueryInstalledAsync(IReadOnlyList<CatalogAppIdentity> catalogIdentities, CancellationToken cancellationToken)
    {
        try
        {
            var installedApps = await trueNasClient.QueryAppsAsync(cancellationToken);
            var matches = new HashSet<CatalogAppIdentity>();
            foreach (var installed in installedApps.Where(app => !app.CustomApp))
            {
                var names = new[] { installed.Id, installed.Name }.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var train = ReadMetadataString(installed.Metadata, "train") ?? ReadMetadataString(installed.Metadata, "catalog_train");
                if (!string.IsNullOrWhiteSpace(train))
                {
                    foreach (var name in names)
                    {
                        var exact = catalogIdentities.FirstOrDefault(identity => identity.Train.Equals(train, StringComparison.OrdinalIgnoreCase) && identity.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (exact is not null)
                        {
                            matches.Add(Normalize(exact));
                        }
                    }

                    continue;
                }

                var candidates = catalogIdentities.Where(identity => names.Any(name => identity.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
                if (candidates.Count == 1)
                {
                    matches.Add(Normalize(candidates[0]));
                }
            }

            return new InstalledMatchSnapshot(true, matches);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Installed app matching was unavailable while loading the catalog");
            return new InstalledMatchSnapshot(false, new HashSet<CatalogAppIdentity>());
        }
    }

    private static bool IsFresh(CatalogDiscoverySnapshot? snapshot, DateTimeOffset now) =>
        snapshot?.RefreshedAtUtc is not null && now - snapshot.RefreshedAtUtc.Value < CacheDuration;

    private static CatalogAppIdentity Normalize(CatalogAppIdentity identity) => new(identity.Train.Trim().ToLowerInvariant(), identity.Name.Trim().ToLowerInvariant());

    private static bool IdentityEquals(CatalogAppIdentity left, CatalogAppIdentity right) =>
        left.Train.Equals(right.Train, StringComparison.OrdinalIgnoreCase) && left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase);

    private static CatalogApp? Find(IEnumerable<CatalogApp> apps, CatalogAppIdentity identity) => apps.FirstOrDefault(app => IdentityEquals(app.Identity, identity));

    private static IReadOnlyList<string> CleanValues(IEnumerable<string> values) => values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    private static string? ReadAdditionalString(TrueNasCatalogAppDto source, string propertyName)
    {
        return source.AdditionalData is not null && source.AdditionalData.TryGetValue(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadNestedAdditionalString(TrueNasCatalogAppDto source, string objectName, string propertyName)
    {
        if (source.AdditionalData is null || !source.AdditionalData.TryGetValue(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return nested.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadAdditionalInt(TrueNasCatalogAppDto source, string propertyName)
    {
        if (source.AdditionalData is null || !source.AdditionalData.TryGetValue(propertyName, out var value))
        {
            return null;
        }

        return value.TryGetInt32(out var result) ? result : null;
    }

    private static IReadOnlyList<string> ReadAdditionalStringList(TrueNasCatalogAppDto source, string propertyName)
    {
        if (source.AdditionalData is null || !source.AdditionalData.TryGetValue(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return CleanValues(value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Where(item => item is not null).Cast<string>());
    }

    private static string? ReadMetadataString(JsonElement metadata, string propertyName) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (CatalogAvailability Availability, string Message) Classify(Exception exception, string diagnosticId)
    {
        var code = exception is TrueNasClientException clientException ? clientException.Code.ToUpperInvariant() : string.Empty;
        var details = $"{code} {exception.Message}";
        if (code == "-32001" || details.Contains("EACCES", StringComparison.OrdinalIgnoreCase) || details.Contains("EPERM", StringComparison.OrdinalIgnoreCase) || details.Contains("NOT AUTHORIZED", StringComparison.OrdinalIgnoreCase) || details.Contains("PERMISSION", StringComparison.OrdinalIgnoreCase))
        {
            return (CatalogAvailability.PermissionDenied, $"The API key cannot read the TrueNAS app catalog. Add CATALOG_READ, then reconnect the API session. APPS_READ alone does not include catalog access. Diagnostic ID: {diagnosticId}.");
        }

        if (details.Contains("NETWORK", StringComparison.OrdinalIgnoreCase) || details.Contains("UNREACHABLE", StringComparison.OrdinalIgnoreCase) || details.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) || details.Contains("CONNECTION", StringComparison.OrdinalIgnoreCase) || details.Contains("DNS", StringComparison.OrdinalIgnoreCase) || exception is HttpRequestException or TimeoutException)
        {
            return (CatalogAvailability.Offline, $"The TrueNAS catalog is unreachable. Check the TrueNAS connection and reconnect. Diagnostic ID: {diagnosticId}.");
        }

        return (CatalogAvailability.Failed, $"The catalog request failed. Reconnect and retry, then search the container logs for diagnostic ID {diagnosticId} if it remains unavailable.");
    }

    private sealed record InstalledMatchSnapshot(bool IsAvailable, HashSet<CatalogAppIdentity> Identities);
}
