using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class DockerHubDiscoveryTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 8, 29, 18, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void CreateSearchPath_NativeFiltersAndSecondPage_UsesDockerHubContract()
    {
        var query = new DockerHubSearchQuery(
            "nginx & proxy",
            DockerHubTrustFilter.Official,
            "web-servers",
            "linux",
            "amd64",
            DockerHubSortOrder.PullCount,
            2,
            24);

        var path = DockerHubDiscoveryService.CreateSearchPath(query);

        StringAssert.Contains(path, "query=nginx%20%26%20proxy");
        StringAssert.Contains(path, "type=image");
        StringAssert.Contains(path, "badges=official");
        StringAssert.Contains(path, "categories=web-servers");
        StringAssert.Contains(path, "operating_systems=linux");
        StringAssert.Contains(path, "architectures=amd64");
        StringAssert.Contains(path, "from=24");
        StringAssert.Contains(path, "size=24");
        StringAssert.Contains(path, "sort=pull_count");
        StringAssert.Contains(path, "order=desc");
    }

    [TestMethod]
    public void NativeFilters_ArchitectureOptions_MatchDockerHubImageFilters()
    {
        var values = DockerHubNativeFilters.Architectures.Select(static option => option.Value).ToArray();

        CollectionAssert.AreEqual(new[] { "amd64", "386", "arm64", "arm", "ppc64", "ppc64le", "s390x" }, values);
    }

    [TestMethod]
    public async Task SearchAsync_PublicImageResponse_MapsRepositoryDetails()
    {
        var handler = new RoutingHttpHandler(_ => JsonResponse(SearchJson));
        var service = CreateService(handler);

        var snapshot = await service.SearchAsync(DefaultQuery());

        Assert.AreEqual(DockerHubAvailability.Available, snapshot.Availability);
        Assert.HasCount(1, snapshot.Repositories);
        var repository = snapshot.Repositories[0];
        Assert.AreEqual("library/nginx", repository.Identity.QualifiedName);
        Assert.AreEqual("nginx", repository.Identity.DisplayName);
        Assert.AreEqual(DockerHubBadge.Official, repository.Badge);
        Assert.AreEqual(1_234_567_890, repository.PullCount);
        Assert.AreEqual("Docker", repository.Publisher);
        CollectionAssert.AreEqual(new[] { "Web Servers" }, repository.Categories.ToArray());
        CollectionAssert.AreEqual(new[] { "Linux" }, repository.OperatingSystems.ToArray());
        StringAssert.StartsWith(repository.DockerHubUrl, "https://hub.docker.com/_/nginx");
        Assert.AreEqual(1, handler.Calls);
        StringAssert.Contains(handler.CapturedUris[0].Query, "query=nginx");
    }

    [TestMethod]
    public async Task SearchAsync_RecentCache_ReusesResponse()
    {
        var handler = new RoutingHttpHandler(_ => JsonResponse(SearchJson));
        var service = CreateService(handler);

        var first = await service.SearchAsync(DefaultQuery());
        var second = await service.SearchAsync(DefaultQuery());

        Assert.AreSame(first, second);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task SearchAsync_RateLimited_ReturnsActionableState()
    {
        var handler = new RoutingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var service = CreateService(handler);

        var snapshot = await service.SearchAsync(DefaultQuery(), true);

        Assert.AreEqual(DockerHubAvailability.RateLimited, snapshot.Availability);
        Assert.HasCount(0, snapshot.Repositories);
        StringAssert.Contains(snapshot.Message, "rate limiting");
        StringAssert.Contains(snapshot.Message, "Diagnostic ID:");
    }

    [TestMethod]
    public async Task GetDetailsAsync_PublicRepositoryAndTags_MapsCustomAppReferenceAndPlatforms()
    {
        var handler = new RoutingHttpHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/v2/namespaces/library/repositories/nginx" => JsonResponse(DetailsJson),
            "/v2/namespaces/library/repositories/nginx/tags" => JsonResponse(TagsJson),
            "/v2/namespaces/library/repositories/nginx/tags/latest" => JsonResponse(LatestTagJson),
            "/api/search/v4" => JsonResponse(SearchJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = CreateService(handler);

        var snapshot = await service.GetDetailsAsync(new DockerHubRepositoryIdentity("library", "nginx"), true);

        Assert.AreEqual(DockerHubAvailability.Available, snapshot.Availability);
        Assert.IsNotNull(snapshot.Repository);
        var repository = snapshot.Repository;
        Assert.AreEqual(DockerHubBadge.Official, repository.Badge);
        Assert.AreEqual("latest", repository.PreferredTag);
        Assert.AreEqual("docker.io/library/nginx:latest", repository.GetImageReference(repository.PreferredTag));
        Assert.HasCount(3, repository.Tags);
        Assert.AreEqual("linux/amd64", repository.Tags[0].Platforms[0].DisplayName);
        Assert.AreEqual("Official overview", repository.ReadmeText);
        Assert.IsFalse(repository.ReadmeText.Contains("alert", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(4, handler.Calls);
    }

    [TestMethod]
    public async Task GetDetailsAsync_UnsafeIdentity_ThrowsArgumentException()
    {
        var service = CreateService(new RoutingHttpHandler(_ => JsonResponse("{}")));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.GetDetailsAsync(new DockerHubRepositoryIdentity("library", "../nginx")));
    }

    private static DockerHubDiscoveryService CreateService(HttpMessageHandler handler) => new(
        new FakeHttpClientFactory(handler, new Uri("https://hub.docker.com/")),
        new CatalogReadmeSanitizer(),
        new FixedTimeProvider(TestNow),
        NullLogger<DockerHubDiscoveryService>.Instance);

    private static DockerHubSearchQuery DefaultQuery() => new("nginx", DockerHubTrustFilter.All, string.Empty, string.Empty, string.Empty, DockerHubSortOrder.BestMatch);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private const string SearchJson = """
        {
          "total": 1,
          "results": [
            {
              "id": "library/nginx",
              "name": "nginx",
              "type": "image",
              "publisher": { "name": "Docker" },
              "created_at": "2014-06-05T12:00:00Z",
              "updated_at": "2026-08-28T16:00:00Z",
              "short_description": "Official NGINX image",
              "badge": "official",
              "star_count": 20000,
              "pull_count": "1B+",
              "raw_pull_count": 1234567890,
              "operating_systems": [{ "name": "linux", "label": "Linux" }],
              "architectures": [{ "name": "amd64", "label": "AMD64" }],
              "logo_url": { "large": "https://djeqr6to3dedg.cloudfront.net/nginx.png" },
              "categories": [{ "slug": "web-servers", "name": "Web Servers" }],
              "archived": false
            }
          ]
        }
        """;

    private const string DetailsJson = """
        {
          "name": "nginx",
          "namespace": "library",
          "description": "Official NGINX image",
          "full_description": "<p>Official overview</p><script>alert('unsafe')</script>",
          "status_description": "active",
          "is_private": false,
          "is_automated": false,
          "star_count": 20000,
          "pull_count": 1234567890,
          "last_updated": "2026-08-28T16:00:00Z",
          "date_registered": "2014-06-05T12:00:00Z",
          "media_types": ["application/vnd.docker.distribution.manifest.list.v2+json"],
          "content_types": ["image"],
          "categories": [{ "slug": "web-servers", "name": "Web Servers" }]
        }
        """;

    private const string TagsJson = """
        {
          "count": 42,
          "results": [
            {
              "name": "stable-trixie-perl",
              "digest": "sha256:recent",
              "full_size": 73400320,
              "tag_last_pushed": "2026-08-28T16:00:00Z",
              "images": [
                { "os": "linux", "architecture": "amd64", "variant": null },
                { "os": "unknown", "architecture": "unknown", "variant": null }
              ]
            },
            {
              "name": "stable",
              "digest": "sha256:def",
              "full_size": 72351744,
              "tag_last_pushed": "2026-08-20T16:00:00Z",
              "images": [{ "os": "linux", "architecture": "arm64", "variant": "v8" }]
            }
          ]
        }
        """;

    private const string LatestTagJson = """
        {
          "name": "latest",
          "digest": "sha256:abc",
          "full_size": 73400320,
          "tag_last_pushed": "2026-08-28T16:00:00Z",
          "images": [{ "os": "linux", "architecture": "amd64", "variant": null }]
        }
        """;

    private sealed class RoutingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        private readonly object sync = new();

        public int Calls { get; private set; }
        public List<Uri> CapturedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                Calls++;
                if (request.RequestUri is not null)
                {
                    CapturedUris.Add(request.RequestUri);
                }
            }

            return Task.FromResult(route(request));
        }
    }
}
