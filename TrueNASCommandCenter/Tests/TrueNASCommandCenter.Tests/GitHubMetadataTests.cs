using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class GitHubMetadataTests
{
    [TestMethod]
    public async Task Refresh_UsesCanonicalGitHubSourceAndTwentyFourHourCache()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => settings.GitHubEnrichmentEnabled = true);
        await using (var seed = await database.CreateDbContextAsync())
        {
            seed.Apps.Add(new AppRecord
            {
                Id = "immich",
                Name = "Immich",
                IsInstalled = true,
                LastSeenUtc = DateTime.UtcNow,
                SourceUrlsJson = JsonSerializer.Serialize(new[] { "https://github.com/immich-app/immich", "https://metadata.example.test/untrusted" })
            });
            await seed.SaveChangesAsync();
        }

        var handler = new SequenceHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"full_name":"immich-app/immich","description":"Photo management","stargazers_count":42000,"license":{"spdx_id":"AGPL-3.0"},"topics":["photos","backup"]}""", Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"repo-etag\"");
            return response;
        });
        var service = new GitHubMetadataService(new FakeHttpClientFactory(handler, new Uri("https://api.github.com/")), database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero)), NullLogger<GitHubMetadataService>.Instance);

        await service.RefreshStaleAsync(["immich"]);
        await service.RefreshStaleAsync(["immich"]);

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(new Uri("https://api.github.com/repos/immich-app/immich"), handler.CapturedUris.Single());
        await using var db = await database.CreateDbContextAsync();
        var cache = await db.GitHubRepositories.SingleAsync();
        Assert.AreEqual("immich-app/immich", cache.FullName);
        Assert.AreEqual(42000, cache.Stars);
        Assert.AreEqual("AGPL-3.0", cache.License);
        Assert.AreEqual("\"repo-etag\"", cache.ETag);
    }
}
