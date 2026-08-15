using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Domain;

namespace TrueNasUpdateManager.Services;

public sealed class SettingsFormModel
{
    public bool OnboardingCompleted { get; set; }
    public string? TrueNasUrl { get; set; }
    public string? TrueNasUsername { get; set; }
    public string? NewTrueNasApiKey { get; set; }
    public bool HasSavedTrueNasApiKey { get; set; }
    public bool VerifyTls { get; set; } = true;
    public bool AllowInsecureWebSocket { get; set; }
    public bool SchedulerEnabled { get; set; }
    public string? CronExpression { get; set; }
    public string? TimeZoneId { get; set; }
    public bool NotifyManualApproval { get; set; }
    public bool NotifyAutomaticFailure { get; set; }
    public bool NotifyAutomaticBlocked { get; set; }
    public bool NotifyRollback { get; set; }
    public bool NotifyAutomaticSuccess { get; set; }
    public bool NotifyScheduledCheckFailure { get; set; }
    public bool NotifyConnectionFailure { get; set; }
    public bool EmailEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public SmtpSecurity? SmtpSecurity { get; set; }
    public string? SmtpUsername { get; set; }
    public string? NewSmtpPassword { get; set; }
    public bool HasSavedSmtpPassword { get; set; }
    public string? EmailFromName { get; set; }
    public string? EmailFromAddress { get; set; }
    public string EmailRecipients { get; set; } = string.Empty;
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public string? NewWebhookAuthorization { get; set; }
    public bool HasSavedWebhookAuthorization { get; set; }
    public string WebhookHeaders { get; set; } = string.Empty;
    public bool HasSavedWebhookHeaders { get; set; }
    public int WebhookTimeoutSeconds { get; set; } = 10;
    public int VerificationTimeoutSeconds { get; set; } = 300;
    public int ConnectionFailureCooldownMinutes { get; set; } = 360;
    public int? HistoryRetentionDays { get; set; }
    public string? ManagerAppId { get; set; }
}

public sealed class SettingsService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISecretProtector secretProtector)
{
    public async Task<SettingsRecord> GetRecordAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Settings.AsNoTracking().SingleAsync(settings => settings.Id == 1, cancellationToken);
    }

    public async Task<SettingsFormModel> GetFormAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetRecordAsync(cancellationToken);
        var recipients = DeserializeStringList(settings.EmailRecipientsJson);

        return new SettingsFormModel
        {
            OnboardingCompleted = settings.OnboardingCompleted,
            TrueNasUrl = settings.TrueNasUrl,
            TrueNasUsername = settings.TrueNasUsername,
            HasSavedTrueNasApiKey = !string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted),
            VerifyTls = settings.VerifyTls,
            AllowInsecureWebSocket = settings.AllowInsecureWebSocket,
            SchedulerEnabled = settings.SchedulerEnabled,
            CronExpression = settings.CronExpression,
            TimeZoneId = settings.TimeZoneId,
            NotifyManualApproval = settings.NotifyManualApproval,
            NotifyAutomaticFailure = settings.NotifyAutomaticFailure,
            NotifyAutomaticBlocked = settings.NotifyAutomaticBlocked,
            NotifyRollback = settings.NotifyRollback,
            NotifyAutomaticSuccess = settings.NotifyAutomaticSuccess,
            NotifyScheduledCheckFailure = settings.NotifyScheduledCheckFailure,
            NotifyConnectionFailure = settings.NotifyConnectionFailure,
            EmailEnabled = settings.EmailEnabled,
            SmtpHost = settings.SmtpHost,
            SmtpPort = settings.SmtpPort,
            SmtpSecurity = settings.SmtpSecurity,
            SmtpUsername = settings.SmtpUsername,
            HasSavedSmtpPassword = !string.IsNullOrWhiteSpace(settings.SmtpPasswordEncrypted),
            EmailFromName = settings.EmailFromName,
            EmailFromAddress = settings.EmailFromAddress,
            EmailRecipients = string.Join(Environment.NewLine, recipients),
            WebhookEnabled = settings.WebhookEnabled,
            WebhookUrl = settings.WebhookUrl,
            HasSavedWebhookAuthorization = !string.IsNullOrWhiteSpace(settings.WebhookAuthorizationEncrypted),
            HasSavedWebhookHeaders = !string.IsNullOrWhiteSpace(settings.WebhookHeadersEncrypted),
            WebhookTimeoutSeconds = settings.WebhookTimeoutSeconds,
            VerificationTimeoutSeconds = settings.VerificationTimeoutSeconds,
            ConnectionFailureCooldownMinutes = settings.ConnectionFailureCooldownMinutes,
            HistoryRetentionDays = settings.HistoryRetentionDays,
            ManagerAppId = settings.ManagerAppId
        };
    }

    public async Task SaveAsync(SettingsFormModel model, CancellationToken cancellationToken = default)
    {
        Validate(model);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);

        settings.OnboardingCompleted = model.OnboardingCompleted;
        settings.TrueNasUrl = NullIfWhiteSpace(model.TrueNasUrl);
        settings.TrueNasUsername = NullIfWhiteSpace(model.TrueNasUsername);
        settings.VerifyTls = model.VerifyTls;
        settings.AllowInsecureWebSocket = model.AllowInsecureWebSocket;
        settings.SchedulerEnabled = model.SchedulerEnabled;
        settings.CronExpression = NullIfWhiteSpace(model.CronExpression);
        settings.TimeZoneId = NullIfWhiteSpace(model.TimeZoneId);
        settings.NotifyManualApproval = model.NotifyManualApproval;
        settings.NotifyAutomaticFailure = model.NotifyAutomaticFailure;
        settings.NotifyAutomaticBlocked = model.NotifyAutomaticBlocked;
        settings.NotifyRollback = model.NotifyRollback;
        settings.NotifyAutomaticSuccess = model.NotifyAutomaticSuccess;
        settings.NotifyScheduledCheckFailure = model.NotifyScheduledCheckFailure;
        settings.NotifyConnectionFailure = model.NotifyConnectionFailure;
        settings.EmailEnabled = model.EmailEnabled;
        settings.SmtpHost = NullIfWhiteSpace(model.SmtpHost);
        settings.SmtpPort = model.SmtpPort;
        settings.SmtpSecurity = model.SmtpSecurity;
        settings.SmtpUsername = NullIfWhiteSpace(model.SmtpUsername);
        settings.EmailFromName = NullIfWhiteSpace(model.EmailFromName);
        settings.EmailFromAddress = NullIfWhiteSpace(model.EmailFromAddress);
        settings.EmailRecipientsJson = JsonSerializer.Serialize(ParseLines(model.EmailRecipients));
        settings.WebhookEnabled = model.WebhookEnabled;
        settings.WebhookUrl = NullIfWhiteSpace(model.WebhookUrl);
        settings.WebhookTimeoutSeconds = model.WebhookTimeoutSeconds;
        settings.VerificationTimeoutSeconds = model.VerificationTimeoutSeconds;
        settings.ConnectionFailureCooldownMinutes = model.ConnectionFailureCooldownMinutes;
        settings.HistoryRetentionDays = model.HistoryRetentionDays;
        settings.ManagerAppId = NullIfWhiteSpace(model.ManagerAppId);

        ReplaceSecret(model.NewTrueNasApiKey, value => settings.TrueNasApiKeyEncrypted = value);
        ReplaceSecret(model.NewSmtpPassword, value => settings.SmtpPasswordEncrypted = value);
        ReplaceSecret(model.NewWebhookAuthorization, value => settings.WebhookAuthorizationEncrypted = value);
        ReplaceSecret(model.WebhookHeaders, value => settings.WebhookHeadersEncrypted = value);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConnectionOptions> GetConnectionOptionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetRecordAsync(cancellationToken);
        if (!TryValidateTrueNasUri(settings.TrueNasUrl, settings.AllowInsecureWebSocket, out var uri, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (string.IsNullOrWhiteSpace(settings.TrueNasUsername) ||
            string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted))
        {
            throw new InvalidOperationException("TrueNAS username and API key are required.");
        }

        return new ConnectionOptions(
            uri!,
            settings.TrueNasUsername,
            secretProtector.Unprotect(settings.TrueNasApiKeyEncrypted),
            settings.VerifyTls,
            settings.AllowInsecureWebSocket);
    }

    public string? ReadSmtpPassword(SettingsRecord settings) =>
        ReadSecret(settings.SmtpPasswordEncrypted);

    public string? ReadWebhookAuthorization(SettingsRecord settings) =>
        ReadSecret(settings.WebhookAuthorizationEncrypted);

    public IReadOnlyDictionary<string, string> ReadWebhookHeaders(SettingsRecord settings)
    {
        var value = ReadSecret(settings.WebhookHeadersEncrypted);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>();
        }

        return ParseHeaders(value);
    }

    public static bool TryValidateTrueNasUri(
        string? value,
        bool allowInsecure,
        out Uri? uri,
        out string? error)
    {
        uri = null;
        error = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeWs && parsed.Scheme != Uri.UriSchemeWss))
        {
            error = "TrueNAS URL must be an absolute ws:// or wss:// URL.";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeWs && !allowInsecure)
        {
            error = "Insecure ws:// requires explicit opt-in.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "TrueNAS URL must not contain embedded credentials.";
            return false;
        }

        var builder = new UriBuilder(parsed);
        if (builder.Path is "" or "/")
        {
            builder.Path = "/api/current";
        }

        if (!string.Equals(builder.Path.TrimEnd('/'), "/api/current", StringComparison.OrdinalIgnoreCase))
        {
            error = "TrueNAS URL path must be /api/current.";
            return false;
        }

        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        uri = builder.Uri;
        return true;
    }

    private static void Validate(SettingsFormModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.TrueNasUrl) &&
            !TryValidateTrueNasUri(model.TrueNasUrl, model.AllowInsecureWebSocket, out _, out var connectionError))
        {
            throw new InvalidOperationException(connectionError);
        }

        if (model.SchedulerEnabled &&
            (string.IsNullOrWhiteSpace(model.CronExpression) || string.IsNullOrWhiteSpace(model.TimeZoneId)))
        {
            throw new InvalidOperationException("An enabled schedule requires a cron expression and timezone.");
        }

        if (model.EmailEnabled)
        {
            if (string.IsNullOrWhiteSpace(model.SmtpHost) ||
                model.SmtpPort is null or < 1 or > 65535 ||
                model.SmtpSecurity is null ||
                string.IsNullOrWhiteSpace(model.EmailFromAddress) ||
                ParseLines(model.EmailRecipients).Count == 0)
            {
                throw new InvalidOperationException("Enabled email requires a host, port, security mode, from address, and recipient.");
            }
        }

        if (!string.IsNullOrWhiteSpace(model.WebhookUrl) &&
            (!Uri.TryCreate(model.WebhookUrl, UriKind.Absolute, out var webhookUri) ||
             (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException("Webhook URL must use HTTP or HTTPS.");
        }

        if (model.WebhookEnabled && string.IsNullOrWhiteSpace(model.WebhookUrl))
        {
            throw new InvalidOperationException("An enabled webhook requires a URL.");
        }

        _ = ParseHeaders(model.WebhookHeaders);

        if (model.WebhookTimeoutSeconds is < 1 or > 120 ||
            model.VerificationTimeoutSeconds is < 30 or > 1800 ||
            model.ConnectionFailureCooldownMinutes is < 1 or > 10080 ||
            model.HistoryRetentionDays is < 1)
        {
            throw new InvalidOperationException("One or more advanced settings are outside the allowed range.");
        }
    }

    private static Dictionary<string, string> ParseHeaders(string? value)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseLines(value))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidOperationException("Webhook headers must use the format Name: Value.");
            }

            var name = line[..separator].Trim();
            var headerValue = line[(separator + 1)..].Trim();
            if (name.ContainsAny('\r', '\n') || headerValue.ContainsAny('\r', '\n'))
            {
                throw new InvalidOperationException("Webhook headers contain invalid characters.");
            }

            headers[name] = headerValue;
        }

        return headers;
    }

    private void ReplaceSecret(string? newValue, Action<string> setter)
    {
        if (!string.IsNullOrWhiteSpace(newValue))
        {
            setter(secretProtector.Protect(newValue.Trim()));
        }
    }

    private string? ReadSecret(string? encrypted) =>
        string.IsNullOrWhiteSpace(encrypted) ? null : secretProtector.Unprotect(encrypted);

    private static List<string> ParseLines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static IReadOnlyList<string> DeserializeStringList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

file static class StringExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) =>
        value.IndexOfAny(characters) >= 0;
}
