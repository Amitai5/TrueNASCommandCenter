using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class CatalogDiscoveryTests
{
    [TestMethod]
    public async Task QueryCatalogAppsAsync_TrueNasResponse_DeserializesTrainsAndUsesReadOnlyOptions()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            Assert.AreEqual("catalog.apps", request.GetProperty("method").GetString());
            var options = request.GetProperty("params")[0];
            Assert.IsFalse(options.GetProperty("cache").GetBoolean());
            Assert.IsFalse(options.GetProperty("cache_only").GetBoolean());
            Assert.IsTrue(options.GetProperty("retrieve_all_trains").GetBoolean());
            transport.Respond(request.GetProperty("id").GetInt64(), new
            {
                community = new Dictionary<string, object>
                {
                    ["immich"] = CatalogPayload("immich", "Immich"),
                    ["netdata"] = CatalogPayload("netdata", "Netdata", new Dictionary<string, long> { ["$date"] = 1_777_201_400_000 })
                }
            });
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var result = await client.QueryCatalogAppsAsync(true);

        Assert.IsTrue(result.ContainsKey("community"));
        Assert.AreEqual("Immich", result["community"]["immich"].Title);
        Assert.AreEqual("NET_BIND_SERVICE", result["community"]["immich"].Capabilities[0].Name);
        Assert.AreEqual(JsonValueKind.Object, result["community"]["netdata"].LastUpdate?.ValueKind);
    }

    /// <summary>Verifies that TrueNAS extended-JSON dates do not prevent the catalog gallery from loading.</summary>
    [TestMethod]
    public async Task GetCatalogAsync_ExtendedJsonLastUpdate_LoadsCatalogAndParsesDate()
    {
        var expected = new DateTimeOffset(2026, 4, 27, 12, 50, 0, TimeSpan.Zero);
        var dto = CatalogDto("netdata", "Netdata") with
        {
            LastUpdate = JsonSerializer.SerializeToElement(new Dictionary<string, long> { ["$date"] = expected.ToUnixTimeMilliseconds() })
        };
        var catalog = new FakeCatalogClient(new Dictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>>
        {
            ["stable"] = new Dictionary<string, TrueNasCatalogAppDto> { ["netdata"] = dto }
        });
        var service = CreateService(catalog, new FakeInstalledClient([]), new FakeDeploymentProvider(AvailableDeployments()));

        var snapshot = await service.GetCatalogAsync(false);

        Assert.AreEqual(CatalogAvailability.Available, snapshot.Availability);
        Assert.HasCount(1, snapshot.Apps);
        Assert.AreEqual(expected, snapshot.Apps[0].LastUpdatedUtc);
    }

    [TestMethod]
    public async Task CatalogDetailsAndSimilarAsync_Identity_UseExpectedReadOnlyParameters()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var method = request.GetProperty("method").GetString();
            var parameters = request.GetProperty("params");
            Assert.AreEqual("immich", parameters[0].GetString());
            if (method == "catalog.get_app_details")
            {
                Assert.AreEqual("community", parameters[1].GetProperty("train").GetString());
                transport.Respond(request.GetProperty("id").GetInt64(), CatalogPayload("immich", "Immich"));
            }
            else
            {
                Assert.AreEqual("app.similar", method);
                Assert.AreEqual("community", parameters[1].GetString());
                transport.Respond(request.GetProperty("id").GetInt64(), new[] { CatalogPayload("photoprism", "PhotoPrism") });
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var details = await client.GetCatalogAppDetailsAsync("immich", "community");
        var similar = await client.QuerySimilarCatalogAppsAsync("immich", "community");

        Assert.AreEqual("Immich", details.Title);
        Assert.HasCount(1, similar);
        Assert.AreEqual("photoprism", similar[0].Name);
    }

    [TestMethod]
    public async Task GetCatalogAsync_DuplicateNamesWithKnownTrain_MarksOnlyExactTrainInstalled()
    {
        var catalog = new FakeCatalogClient(CatalogWithDuplicateNames());
        var installed = new FakeInstalledClient([
            new TrueNasAppDto
            {
                Id = "shared-name",
                Name = "shared-name",
                Metadata = JsonSerializer.SerializeToElement(new { train = "community" })
            }
        ]);
        var service = CreateService(catalog, installed, new FakeDeploymentProvider(AvailableDeployments()));

        var snapshot = await service.GetCatalogAsync(false);

        Assert.HasCount(2, snapshot.Apps);
        Assert.IsTrue(snapshot.Apps.Single(app => app.Identity.Train == "community").IsInstalled);
        Assert.IsFalse(snapshot.Apps.Single(app => app.Identity.Train == "stable").IsInstalled);
    }

    [TestMethod]
    public async Task GetCatalogAsync_DuplicateNamesWithoutTrain_DoesNotMergeInstalledState()
    {
        var catalog = new FakeCatalogClient(CatalogWithDuplicateNames());
        var installed = new FakeInstalledClient([new TrueNasAppDto { Id = "shared-name", Name = "shared-name" }]);
        var service = CreateService(catalog, installed, new FakeDeploymentProvider(AvailableDeployments()));

        var snapshot = await service.GetCatalogAsync(false);

        Assert.IsTrue(snapshot.Apps.All(app => app.IsInstalled is false));
    }

    [TestMethod]
    public async Task GetCatalogAsync_TelemetryUnavailable_LeavesDeploymentCountsNull()
    {
        var catalog = new FakeCatalogClient(SingleCatalog());
        var deployments = new ActiveDeploymentSnapshot(new Dictionary<CatalogAppIdentity, long>(), null, false, Error: "offline");
        var service = CreateService(catalog, new FakeInstalledClient([]), new FakeDeploymentProvider(deployments));

        var snapshot = await service.GetCatalogAsync(false);

        Assert.IsNull(snapshot.Apps[0].ActiveDeployments);
        Assert.AreEqual(CatalogAvailability.Available, snapshot.Availability);
    }

    [TestMethod]
    public async Task GetCatalogAsync_RefreshFailure_PreservesLastSuccessfulSnapshot()
    {
        var catalog = new FakeCatalogClient(SingleCatalog());
        var service = CreateService(catalog, new FakeInstalledClient([]), new FakeDeploymentProvider(AvailableDeployments()));
        var original = await service.GetCatalogAsync(false);
        catalog.QueryException = new TrueNasClientException("NETWORK_UNREACHABLE", "No route to TrueNAS.");

        var refreshed = await service.GetCatalogAsync(true);

        Assert.HasCount(1, refreshed.Apps);
        Assert.AreEqual(original.Apps[0].Identity, refreshed.Apps[0].Identity);
        Assert.IsTrue(refreshed.IsStale);
        StringAssert.Contains(refreshed.Message, "Showing the last successful results");
    }

    [TestMethod]
    public async Task GetCatalogAsync_PermissionFailureWithoutCache_ReturnsPermissionState()
    {
        var catalog = new FakeCatalogClient(SingleCatalog())
        {
            QueryException = new TrueNasClientException("-32001", "[EACCES] Not authorized")
        };
        var service = CreateService(catalog, new FakeInstalledClient([]), new FakeDeploymentProvider(AvailableDeployments()));

        var snapshot = await service.GetCatalogAsync(false);

        Assert.AreEqual(CatalogAvailability.PermissionDenied, snapshot.Availability);
        Assert.HasCount(0, snapshot.Apps);
        StringAssert.Contains(snapshot.Message, "CATALOG_READ");
        StringAssert.Contains(snapshot.Message, "Diagnostic ID:");
    }

    [TestMethod]
    public async Task ReconnectAndRefreshAsync_UpdatedPrivilege_ReauthenticatesBeforeRetry()
    {
        var catalog = new FakeCatalogClient(SingleCatalog())
        {
            QueryException = new TrueNasClientException("-32001", "TrueNAS rejected the request")
        };
        var installed = new FakeInstalledClient([]);
        var service = CreateService(catalog, installed, new FakeDeploymentProvider(AvailableDeployments()));
        var denied = await service.GetCatalogAsync(false);
        catalog.QueryException = null;

        var refreshed = await service.ReconnectAndRefreshAsync();

        Assert.AreEqual(CatalogAvailability.PermissionDenied, denied.Availability);
        Assert.AreEqual(1, installed.ResetCount);
        Assert.AreEqual(CatalogAvailability.Available, refreshed.Availability);
        Assert.HasCount(1, refreshed.Apps);
    }

    [TestMethod]
    public void Apply_SearchFiltersAndActiveDeploymentSort_ReturnsExpectedApps()
    {
        var apps = new[]
        {
            App("community", "alpha", "Alpha Photos", ["photos"], ["backup"], true, 100),
            App("stable", "beta", "Beta Media", ["media"], ["streaming"], false, 900),
            App("community", "gamma", "Gamma Photos", ["photos"], ["gallery"], false, null)
        };
        var query = new CatalogGalleryQuery("photo", "photos", "community", CatalogPresenceFilter.All, CatalogPresenceFilter.All, CatalogSortOrder.ActiveDeployments);

        var result = CatalogGalleryQueryEngine.Apply(apps, query);

        CollectionAssert.AreEqual(new[] { "alpha", "gamma" }, result.Select(app => app.Identity.Name).ToArray());
    }

    [TestMethod]
    public void Apply_NullPopularityRanks_SortsKnownRanksFirstWithoutUsingDeploymentCounts()
    {
        var highDeployments = App("community", "high", "High", [], [], false, 99_000) with { PopularityRank = null };
        var ranked = App("community", "ranked", "Ranked", [], [], false, 10) with { PopularityRank = 2 };
        var query = new CatalogGalleryQuery(string.Empty, string.Empty, string.Empty, CatalogPresenceFilter.All, CatalogPresenceFilter.All, CatalogSortOrder.Popularity);

        var result = CatalogGalleryQueryEngine.Apply([highDeployments, ranked], query);

        CollectionAssert.AreEqual(new[] { "ranked", "high" }, result.Select(app => app.Identity.Name).ToArray());
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("ftp://example.test/app")]
    [DataRow("https://user:secret@example.test/app")]
    [DataRow("/relative/path")]
    public void NormalizeExternalUrl_UnsafeUrl_ReturnsNull(string value)
    {
        var service = new CatalogLinkService();

        var result = service.NormalizeExternalUrl(value);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetTrueNasAppsUrl_OfficialMetadataLink_PrefersProvidedCatalogRoute()
    {
        var service = new CatalogLinkService();

        var result = service.GetTrueNasAppsUrl(["https://github.com/example/app", "https://apps.truenas.com/catalog/example_community/"]);

        Assert.AreEqual("https://apps.truenas.com/catalog/example_community/", result);
    }

    [TestMethod]
    public void GetTrueNasAppsUrl_NoOfficialRoute_UsesCatalogRootWithoutGuessing()
    {
        var service = new CatalogLinkService();

        var result = service.GetTrueNasAppsUrl(["https://github.com/example/app"]);

        Assert.AreEqual("https://apps.truenas.com/catalog/", result);
    }

    [TestMethod]
    public void Sanitize_UntrustedReadme_ReturnsReadablePlainTextWithoutMarkupOrScriptContent()
    {
        var service = new CatalogReadmeSanitizer();

        var result = service.Sanitize("<h1>Safe title</h1><script>alert('x')</script><p>Hello &amp; goodbye</p><a href=\"javascript:alert(2)\">Link text</a>");

        StringAssert.Contains(result, "Safe title");
        StringAssert.Contains(result, "Hello & goodbye");
        StringAssert.Contains(result, "Link text");
        Assert.IsFalse(result.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Contains("alert", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GetAsync_OfficialTelemetryResponse_MapsTrainAndAppCounts()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Community\":{\"Immich\":12345},\"Stable\":{\"Plex\":\"6789\"}}", Encoding.UTF8, "application/json")
        };
        var provider = new TrueNasActiveDeploymentProvider(
            new FakeHttpClientFactory(new SequenceHttpHandler(_ => response)),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<TrueNasActiveDeploymentProvider>.Instance);

        var snapshot = await provider.GetAsync(true);

        Assert.IsTrue(snapshot.IsAvailable);
        Assert.AreEqual(12_345, snapshot.Counts[TrueNasActiveDeploymentProvider.Normalize("community", "immich")]);
        Assert.AreEqual(6_789, snapshot.Counts[TrueNasActiveDeploymentProvider.Normalize("stable", "plex")]);
    }

    [TestMethod]
    public async Task GetAsync_RefreshFailureAfterSuccess_ReturnsStaleCounts()
    {
        var provider = new TrueNasActiveDeploymentProvider(
            new FakeHttpClientFactory(new SequenceHttpHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Community\":{\"Immich\":123}}") },
                _ => throw new HttpRequestException("offline"))),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<TrueNasActiveDeploymentProvider>.Instance);
        _ = await provider.GetAsync(true);

        var stale = await provider.GetAsync(true);

        Assert.IsTrue(stale.IsAvailable);
        Assert.IsTrue(stale.IsStale);
        Assert.AreEqual(123, stale.Counts[TrueNasActiveDeploymentProvider.Normalize("community", "immich")]);
    }

    private static CatalogDiscoveryService CreateService(ITrueNasCatalogClient catalog, ITrueNasClient installed, IActiveDeploymentProvider deployments) => new(
        catalog,
        installed,
        deployments,
        new CatalogLinkService(),
        new CatalogReadmeSanitizer(),
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero)),
        NullLogger<CatalogDiscoveryService>.Instance);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>> SingleCatalog() =>
        new Dictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["community"] = new Dictionary<string, TrueNasCatalogAppDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["immich"] = CatalogDto("immich", "Immich")
            }
        };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>> CatalogWithDuplicateNames() =>
        new Dictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["community"] = new Dictionary<string, TrueNasCatalogAppDto> { ["shared-name"] = CatalogDto("shared-name", "Community app") },
            ["stable"] = new Dictionary<string, TrueNasCatalogAppDto> { ["shared-name"] = CatalogDto("shared-name", "Stable app") }
        };

    private static ActiveDeploymentSnapshot AvailableDeployments() => new(
        new Dictionary<CatalogAppIdentity, long>
        {
            [TrueNasActiveDeploymentProvider.Normalize("community", "immich")] = 42
        },
        new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
        true);

    private static TrueNasCatalogAppDto CatalogDto(string name, string title) => new()
    {
        Name = name,
        Title = title,
        Description = $"{title} description",
        Healthy = true,
        LatestVersion = "1.0.0",
        LatestAppVersion = "2.0.0",
        LastUpdate = JsonSerializer.SerializeToElement("2026-08-27 12:30:00"),
        Categories = ["photos"],
        Tags = ["backup"],
        Sources = [$"https://apps.truenas.com/catalog/{name}_community/"]
    };

    private static CatalogApp App(string train, string name, string title, IReadOnlyList<string> categories, IReadOnlyList<string> tags, bool installed, long? deployments) => new()
    {
        Identity = new CatalogAppIdentity(train, name),
        Title = title,
        Description = $"{title} description",
        Categories = categories,
        Tags = tags,
        IsInstalled = installed,
        IsCatalogHealthy = true,
        ActiveDeployments = deployments,
        TrueNasAppsUrl = "https://apps.truenas.com/catalog/"
    };

    private static object CatalogPayload(string name, string title, object? lastUpdate = null) => new
    {
        app_readme = "<p>README</p>",
        categories = new[] { "photos" },
        description = "Description",
        healthy = true,
        healthy_error = (string?)null,
        home = "https://example.test",
        location = "/catalog/app",
        latest_version = "1.0.0",
        latest_app_version = "2.0.0",
        latest_human_version = "2.0.0_1.0.0",
        last_update = lastUpdate ?? "2026-08-27 12:30:00",
        name,
        recommended = true,
        title,
        maintainers = new[] { new { name = "TrueNAS", email = "dev@truenas.com", url = "https://www.truenas.com" } },
        tags = new[] { "backup" },
        screenshots = Array.Empty<string>(),
        sources = new[] { $"https://apps.truenas.com/catalog/{name}_community/" },
        icon_url = "https://media.sys.truenas.net/apps/example/icon.svg",
        capabilities = new[] { new { name = "NET_BIND_SERVICE", description = "Bind ports" } },
        run_as_context = Array.Empty<object>()
    };

    private sealed class FakeCatalogClient(IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>> catalog) : ITrueNasCatalogClient
    {
        public Exception? QueryException { get; set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>>> QueryCatalogAppsAsync(bool forceRefresh, CancellationToken cancellationToken = default) =>
            QueryException is null ? Task.FromResult(catalog) : Task.FromException<IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>>>(QueryException);

        public Task<TrueNasCatalogAppDto> GetCatalogAppDetailsAsync(string appName, string train, CancellationToken cancellationToken = default) => Task.FromResult(catalog[train][appName]);

        public Task<IReadOnlyList<TrueNasCatalogAppDto>> QuerySimilarCatalogAppsAsync(string appName, string train, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrueNasCatalogAppDto>>([]);
    }

    private sealed class FakeDeploymentProvider(ActiveDeploymentSnapshot snapshot) : IActiveDeploymentProvider
    {
        public Task<ActiveDeploymentSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class FakeInstalledClient(IReadOnlyList<TrueNasAppDto> apps) : ITrueNasClient
    {
        public bool? HasWriteAccess => true;
        public int ResetCount { get; private set; }

        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) => Task.FromResult(apps);
        public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(string appId, string targetVersion = "latest", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartUpgradeAsync(string appId, string targetVersion, bool snapshotHostPaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartRollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task ResetConnectionAsync()
        {
            ResetCount++;
            return Task.CompletedTask;
        }
    }
}
