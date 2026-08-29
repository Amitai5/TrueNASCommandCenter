using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.DockerHub;

internal sealed record DockerHubSearchResponse
{
    [JsonPropertyName("total")]
    public long? Total { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<DockerHubSearchRepositoryDto>? Results { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed record DockerHubSearchRepositoryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("publisher")]
    public DockerHubPublisherDto? Publisher { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("short_description")]
    public string? ShortDescription { get; init; }

    [JsonPropertyName("badge")]
    public string? Badge { get; init; }

    [JsonPropertyName("star_count")]
    public long StarCount { get; init; }

    [JsonPropertyName("pull_count")]
    public string? PullCount { get; init; }

    [JsonPropertyName("raw_pull_count")]
    public long? RawPullCount { get; init; }

    [JsonPropertyName("operating_systems")]
    public IReadOnlyList<DockerHubNamedValueDto>? OperatingSystems { get; init; }

    [JsonPropertyName("architectures")]
    public IReadOnlyList<DockerHubNamedValueDto>? Architectures { get; init; }

    [JsonPropertyName("logo_url")]
    public DockerHubLogoDto? LogoUrl { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<DockerHubCategoryDto>? Categories { get; init; }

    [JsonPropertyName("archived")]
    public bool Archived { get; init; }

    [JsonPropertyName("media_types")]
    public IReadOnlyList<string>? MediaTypes { get; init; }

    [JsonPropertyName("content_types")]
    public IReadOnlyList<string>? ContentTypes { get; init; }
}

internal sealed record DockerHubPublisherDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record DockerHubNamedValueDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

internal sealed record DockerHubCategoryDto
{
    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record DockerHubLogoDto
{
    [JsonPropertyName("large")]
    public string? Large { get; init; }

    [JsonPropertyName("small")]
    public string? Small { get; init; }
}

internal sealed record DockerHubRepositoryDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("full_description")]
    public string? FullDescription { get; init; }

    [JsonPropertyName("status_description")]
    public string? StatusDescription { get; init; }

    [JsonPropertyName("is_private")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("is_automated")]
    public bool IsAutomated { get; init; }

    [JsonPropertyName("star_count")]
    public long StarCount { get; init; }

    [JsonPropertyName("pull_count")]
    public long? PullCount { get; init; }

    [JsonPropertyName("last_updated")]
    public DateTimeOffset? LastUpdated { get; init; }

    [JsonPropertyName("date_registered")]
    public DateTimeOffset? DateRegistered { get; init; }

    [JsonPropertyName("media_types")]
    public IReadOnlyList<string>? MediaTypes { get; init; }

    [JsonPropertyName("content_types")]
    public IReadOnlyList<string>? ContentTypes { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<DockerHubCategoryDto>? Categories { get; init; }
}

internal sealed record DockerHubTagPageDto
{
    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<DockerHubTagDto>? Results { get; init; }
}

internal sealed record DockerHubTagDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }

    [JsonPropertyName("full_size")]
    public long? FullSize { get; init; }

    [JsonPropertyName("tag_last_pushed")]
    public DateTimeOffset? TagLastPushed { get; init; }

    [JsonPropertyName("last_updated")]
    public DateTimeOffset? LastUpdated { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<DockerHubTagImageDto>? Images { get; init; }
}

internal sealed record DockerHubTagImageDto
{
    [JsonPropertyName("os")]
    public string? OperatingSystem { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("variant")]
    public string? Variant { get; init; }
}
