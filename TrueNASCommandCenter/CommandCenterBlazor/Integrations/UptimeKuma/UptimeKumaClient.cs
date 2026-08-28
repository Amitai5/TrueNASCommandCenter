using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Integrations.UptimeKuma;

public interface IUptimeKumaClient
{
    /// <summary>Tests the saved Uptime Kuma connection and parses its monitor metrics.</summary>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The connection result and discovered monitor count.</returns>
    Task<UptimeKumaConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads and parses all monitor metrics from the saved Uptime Kuma connection.</summary>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The current Uptime Kuma monitor metrics.</returns>
    Task<IReadOnlyList<UptimeKumaMonitorMetric>> GetMonitorMetricsAsync(CancellationToken cancellationToken = default);
}

public sealed class UptimeKumaClient(IHttpClientFactory httpClientFactory, SettingsService settingsService, UptimeKumaMetricsParser parser) : IUptimeKumaClient
{
    private const int MaximumMetricsBytes = 5 * 1024 * 1024;

    /// <inheritdoc cref="IUptimeKumaClient.TestConnectionAsync"/>
    public async Task<UptimeKumaConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var monitors = await GetMonitorMetricsAsync(cancellationToken);
            return new UptimeKumaConnectionTestResult(true, $"Connected to Uptime Kuma. {monitors.Count} monitor{(monitors.Count == 1 ? string.Empty : "s")} available.", monitors.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UptimeKumaConnectionTestResult(false, "The Uptime Kuma connection timed out.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            return new UptimeKumaConnectionTestResult(false, exception.Message);
        }
    }

    /// <inheritdoc cref="IUptimeKumaClient.GetMonitorMetricsAsync"/>
    public async Task<IReadOnlyList<UptimeKumaMonitorMetric>> GetMonitorMetricsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetRecordAsync(cancellationToken);
        var baseUri = ParseBaseUri(settings.UptimeKumaBaseUrl, "Uptime Kuma connection URL");
        var client = httpClientFactory.CreateClient(settings.UptimeKumaVerifyTls ? "uptime-kuma" : "uptime-kuma-insecure");
        var metricsUri = new Uri(baseUri, "metrics");
        using var request = new HttpRequestMessage(HttpMethod.Get, metricsUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        var apiKey = settingsService.ReadUptimeKumaApiKey(settings);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{apiKey}")));
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Uptime Kuma rejected the API key. Create a Prometheus API key in Uptime Kuma Settings → Security → API Keys.");
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidOperationException("Uptime Kuma redirected the metrics request. Enter the final connection URL used by the server.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Uptime Kuma returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        return parser.Parse(await ReadLimitedStringAsync(response.Content, cancellationToken));
    }

    /// <summary>Validates and normalizes an Uptime Kuma HTTP or HTTPS base URL.</summary>
    /// <param name="value">The configured absolute base URL.</param>
    /// <param name="label">The user-facing field label.</param>
    /// <returns>The normalized base URL ending in a slash.</returns>
    public static Uri ParseBaseUri(string? value, string label)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{label} must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.");
        }

        return new UriBuilder(uri) { Path = $"{uri.AbsolutePath.TrimEnd('/')}/" }.Uri;
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumMetricsBytes)
        {
            throw new InvalidOperationException("The Uptime Kuma metrics response exceeded the 5 MB safety limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken) is var count && count > 0)
        {
            if (result.Length + count > MaximumMetricsBytes)
            {
                throw new InvalidOperationException("The Uptime Kuma metrics response exceeded the 5 MB safety limit.");
            }

            await result.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
    }
}
