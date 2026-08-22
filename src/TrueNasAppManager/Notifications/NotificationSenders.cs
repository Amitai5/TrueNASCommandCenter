using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Notifications;

public interface IEmailNotificationSender
{
    Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default);
}

public interface IWebhookNotificationSender
{
    Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default);
}

public static class EmailMessageFactory
{
    /// <summary>Creates the plain-text message submitted to the TrueNAS mail service.</summary>
    /// <param name="notification">The notification event to format.</param>
    /// <param name="recipients">Optional explicit recipients; an empty list uses TrueNAS administrators.</param>
    /// <returns>The TrueNAS mail request.</returns>
    public static TrueNasMailMessage Create(NotificationEvent notification, IReadOnlyList<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(recipients);
        return new TrueNasMailMessage(notification.Subject, $"{notification.Message}{Environment.NewLine}{Environment.NewLine}Reason: {notification.ReasonCode}", recipients);
    }
}

public sealed class EmailNotificationSender(
    SettingsService settingsService,
    ITrueNasClient trueNasClient,
    ILogger<EmailNotificationSender> logger) : IEmailNotificationSender
{
    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsService.GetRecordAsync(cancellationToken);
            var recipients = string.IsNullOrWhiteSpace(settings.EmailRecipientsJson)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(settings.EmailRecipientsJson) ?? [];
            await trueNasClient.SendMailAsync(EmailMessageFactory.Create(notification, recipients), cancellationToken);
            return new NotificationDeliveryResult(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Email delivery failed: {ErrorType}", exception.GetType().Name);
            return new NotificationDeliveryResult(false, Error: "Email delivery failed.");
        }
    }
}

public sealed class WebhookNotificationSender(
    IHttpClientFactory httpClientFactory,
    SettingsService settingsService,
    TrueNasEndpointOptions trueNasEndpoint,
    TimeProvider timeProvider,
    ILogger<WebhookNotificationSender> logger) : IWebhookNotificationSender
{
    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetRecordAsync(cancellationToken);
        if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return new NotificationDeliveryResult(false, Error: "Webhook is not fully configured.");
        }

        var payload = new
        {
            schemaVersion = 1,
            eventId = notification.EventId,
            @event = notification.EventType.ToString(),
            timestamp = notification.TimestampUtc,
            server = new
            {
                name = trueNasEndpoint.ServerUri.Host,
                url = trueNasEndpoint.ServerUrl
            },
            app = notification.AppId is null
                ? null
                : new
                {
                    id = notification.AppId,
                    name = notification.AppName,
                    installedVersion = notification.InstalledVersion,
                    availableVersionOrImages = notification.AvailableVersionOrImages
                },
            reason = new
            {
                code = notification.ReasonCode,
                message = notification.Message
            }
        };

        var authorization = settingsService.ReadWebhookAuthorization(settings);
        var headers = settingsService.ReadWebhookHeaders(settings);
        var client = httpClientFactory.CreateClient("webhook");
        client.Timeout = TimeSpan.FromSeconds(settings.WebhookTimeoutSeconds);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload)
                };

                if (!string.IsNullOrWhiteSpace(authorization))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", authorization);
                }

                foreach (var header in headers)
                {
                    if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                using var response = await client.SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return new NotificationDeliveryResult(true, status);
                }

                if (!IsRetryable(response.StatusCode) || attempt == 3)
                {
                    return new NotificationDeliveryResult(false, status, $"Webhook returned HTTP {status}.");
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                if (attempt == 3)
                {
                    logger.LogWarning("Webhook delivery failed after retries: {ErrorType}", exception.GetType().Name);
                    return new NotificationDeliveryResult(false, Error: "Webhook delivery failed after retries.");
                }
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Min(2000, 250 * Math.Pow(2, attempt))),
                timeProvider,
                cancellationToken);
        }

        return new NotificationDeliveryResult(false, Error: "Webhook delivery failed.");
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 ||
        (int)statusCode >= 500;
}
