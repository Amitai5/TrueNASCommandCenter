using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Services;

namespace TrueNasUpdateManager.Notifications;

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
    public static MimeMessage Create(
        NotificationEvent notification,
        string? fromName,
        string fromAddress,
        IEnumerable<string> recipients)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName ?? "TrueNAS App Update Manager", fromAddress));
        foreach (var recipient in recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = notification.Subject;
        message.Body = new BodyBuilder
        {
            TextBody = notification.Message,
            HtmlBody = $"""
                <!doctype html>
                <html lang="en">
                <body style="font-family:system-ui,sans-serif;color:#1f2937">
                  <h2>{WebUtility.HtmlEncode(notification.Subject)}</h2>
                  <p>{WebUtility.HtmlEncode(notification.Message)}</p>
                  <p style="color:#6b7280">Reason: {WebUtility.HtmlEncode(notification.ReasonCode)}</p>
                </body>
                </html>
                """
        }.ToMessageBody();
        return message;
    }
}

public sealed class EmailNotificationSender(
    SettingsService settingsService,
    ILogger<EmailNotificationSender> logger) : IEmailNotificationSender
{
    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsService.GetRecordAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.SmtpHost) ||
                settings.SmtpPort is null ||
                settings.SmtpSecurity is null ||
                string.IsNullOrWhiteSpace(settings.EmailFromAddress))
            {
                return new NotificationDeliveryResult(false, Error: "Email is not fully configured.");
            }

            var recipients = string.IsNullOrWhiteSpace(settings.EmailRecipientsJson)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(settings.EmailRecipientsJson) ?? [];
            if (recipients.Count == 0)
            {
                return new NotificationDeliveryResult(false, Error: "Email has no recipients.");
            }

            var message = EmailMessageFactory.Create(
                notification,
                settings.EmailFromName,
                settings.EmailFromAddress,
                recipients);

            using var smtp = new SmtpClient();
            smtp.Timeout = 30_000;
            await smtp.ConnectAsync(
                settings.SmtpHost,
                settings.SmtpPort.Value,
                settings.SmtpSecurity switch
                {
                    SmtpSecurity.None => SecureSocketOptions.None,
                    SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
                    SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                    _ => SecureSocketOptions.Auto
                },
                cancellationToken);

            var password = settingsService.ReadSmtpPassword(settings);
            if (!string.IsNullOrWhiteSpace(settings.SmtpUsername) && password is not null)
            {
                await smtp.AuthenticateAsync(settings.SmtpUsername, password, cancellationToken);
            }

            await smtp.SendAsync(message, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
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

        var trueNasHost = Uri.TryCreate(settings.TrueNasUrl, UriKind.Absolute, out var trueNasUri)
            ? trueNasUri.Host
            : null;
        var payload = new
        {
            schemaVersion = 1,
            eventId = notification.EventId,
            @event = notification.EventType.ToString(),
            timestamp = notification.TimestampUtc,
            server = new
            {
                name = trueNasHost,
                url = settings.TrueNasUrl
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
