using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.UptimeKuma;

namespace TrueNasAppManager.Services;

public sealed class SettingsFormModel
{
    public bool OnboardingCompleted { get; set; }
    public string? TrueNasUsername { get; set; }
    public string? NewTrueNasApiKey { get; set; }
    public bool HasSavedTrueNasApiKey { get; set; }
    public bool VerifyTls { get; set; } = true;
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
    public string EmailRecipients { get; set; } = string.Empty;
    public string? PortalHostOverride { get; set; }
    public bool GitHubEnrichmentEnabled { get; set; }
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
    public bool UptimeKumaEnabled { get; set; }
    public string? UptimeKumaBaseUrl { get; set; }
    public string? UptimeKumaBrowserUrl { get; set; }
    public string? NewUptimeKumaApiKey { get; set; }
    public bool HasSavedUptimeKumaApiKey { get; set; }
    public bool UptimeKumaVerifyTls { get; set; } = true;
    public int UptimeKumaRefreshIntervalSeconds { get; set; } = 60;
}

public sealed class SettingsService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISecretProtector secretProtector,
    TrueNasEndpointOptions trueNasEndpoint)
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
            TrueNasUsername = settings.TrueNasUsername,
            HasSavedTrueNasApiKey = !string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted),
            VerifyTls = settings.VerifyTls,
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
            EmailRecipients = string.Join(Environment.NewLine, recipients),
            PortalHostOverride = settings.PortalHostOverride,
            GitHubEnrichmentEnabled = settings.GitHubEnrichmentEnabled,
            WebhookEnabled = settings.WebhookEnabled,
            WebhookUrl = settings.WebhookUrl,
            HasSavedWebhookAuthorization = !string.IsNullOrWhiteSpace(settings.WebhookAuthorizationEncrypted),
            HasSavedWebhookHeaders = !string.IsNullOrWhiteSpace(settings.WebhookHeadersEncrypted),
            WebhookTimeoutSeconds = settings.WebhookTimeoutSeconds,
            VerificationTimeoutSeconds = settings.VerificationTimeoutSeconds,
            ConnectionFailureCooldownMinutes = settings.ConnectionFailureCooldownMinutes,
            HistoryRetentionDays = settings.HistoryRetentionDays,
            ManagerAppId = settings.ManagerAppId,
            UptimeKumaEnabled = settings.UptimeKumaEnabled,
            UptimeKumaBaseUrl = settings.UptimeKumaBaseUrl,
            UptimeKumaBrowserUrl = settings.UptimeKumaBrowserUrl,
            HasSavedUptimeKumaApiKey = !string.IsNullOrWhiteSpace(settings.UptimeKumaApiKeyEncrypted),
            UptimeKumaVerifyTls = settings.UptimeKumaVerifyTls,
            UptimeKumaRefreshIntervalSeconds = settings.UptimeKumaRefreshIntervalSeconds
        };
    }

    public async Task SaveAsync(SettingsFormModel model, CancellationToken cancellationToken = default)
    {
        Validate(model);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);

        settings.OnboardingCompleted = model.OnboardingCompleted;
        settings.TrueNasUrl = trueNasEndpoint.ServerUrl;
        settings.TrueNasUsername = NullIfWhiteSpace(model.TrueNasUsername);
        settings.VerifyTls = model.VerifyTls;
        settings.AllowInsecureWebSocket = false;
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
        settings.EmailRecipientsJson = JsonSerializer.Serialize(ParseLines(model.EmailRecipients));
        settings.PortalHostOverride = NormalizePortalHost(model.PortalHostOverride);
        settings.GitHubEnrichmentEnabled = model.GitHubEnrichmentEnabled;
        settings.WebhookEnabled = model.WebhookEnabled;
        settings.WebhookUrl = NullIfWhiteSpace(model.WebhookUrl);
        settings.WebhookTimeoutSeconds = model.WebhookTimeoutSeconds;
        settings.VerificationTimeoutSeconds = model.VerificationTimeoutSeconds;
        settings.ConnectionFailureCooldownMinutes = model.ConnectionFailureCooldownMinutes;
        settings.HistoryRetentionDays = model.HistoryRetentionDays;
        settings.ManagerAppId = NullIfWhiteSpace(model.ManagerAppId);
        settings.UptimeKumaEnabled = model.UptimeKumaEnabled;
        settings.UptimeKumaBaseUrl = NormalizeOptionalUptimeKumaUrl(model.UptimeKumaBaseUrl, "Uptime Kuma connection URL");
        settings.UptimeKumaBrowserUrl = NormalizeOptionalUptimeKumaUrl(model.UptimeKumaBrowserUrl, "Uptime Kuma browser URL");
        settings.UptimeKumaVerifyTls = model.UptimeKumaVerifyTls;
        settings.UptimeKumaRefreshIntervalSeconds = model.UptimeKumaRefreshIntervalSeconds;

        ReplaceSecret(model.NewTrueNasApiKey, value => settings.TrueNasApiKeyEncrypted = value);
        ReplaceSecret(model.NewWebhookAuthorization, value => settings.WebhookAuthorizationEncrypted = value);
        ReplaceSecret(model.WebhookHeaders, value => settings.WebhookHeadersEncrypted = value);
        ReplaceSecret(model.NewUptimeKumaApiKey, value => settings.UptimeKumaApiKeyEncrypted = value);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConnectionOptions> GetConnectionOptionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetRecordAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.TrueNasUsername) ||
            string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted))
        {
            throw new InvalidOperationException("TrueNAS username and API key are required.");
        }

        return new ConnectionOptions(
            trueNasEndpoint.ServerUri,
            settings.TrueNasUsername,
            secretProtector.Unprotect(settings.TrueNasApiKeyEncrypted),
            settings.VerifyTls);
    }

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

    /// <summary>Decrypts the saved Uptime Kuma Prometheus API key when one is configured.</summary>
    /// <param name="settings">The persisted settings record containing the protected key.</param>
    /// <returns>The plaintext key for an outbound request, or null when no key is configured.</returns>
    public string? ReadUptimeKumaApiKey(SettingsRecord settings) => ReadSecret(settings.UptimeKumaApiKeyEncrypted);

    private static void Validate(SettingsFormModel model)
    {
        if (model.SchedulerEnabled &&
            (string.IsNullOrWhiteSpace(model.CronExpression) || string.IsNullOrWhiteSpace(model.TimeZoneId)))
        {
            throw new InvalidOperationException("An enabled schedule requires a cron expression and timezone.");
        }

        foreach (var recipient in ParseLines(model.EmailRecipients))
        {
            try
            {
                _ = new System.Net.Mail.MailAddress(recipient);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"'{recipient}' is not a valid email recipient.");
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

        _ = NormalizeOptionalUptimeKumaUrl(model.UptimeKumaBaseUrl, "Uptime Kuma connection URL");
        _ = NormalizeOptionalUptimeKumaUrl(model.UptimeKumaBrowserUrl, "Uptime Kuma browser URL");
        if (model.UptimeKumaEnabled && string.IsNullOrWhiteSpace(model.UptimeKumaBaseUrl))
        {
            throw new InvalidOperationException("An enabled Uptime Kuma integration requires a connection URL.");
        }

        if (model.WebhookTimeoutSeconds is < 1 or > 120 ||
            model.VerificationTimeoutSeconds is < 30 or > 1800 ||
            model.ConnectionFailureCooldownMinutes is < 1 or > 10080 ||
            model.HistoryRetentionDays is < 1 ||
            model.UptimeKumaRefreshIntervalSeconds is < 30 or > 3600)
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

    private static string? NormalizePortalHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Portal host override must be an HTTP or HTTPS origin without a query or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? NormalizeOptionalUptimeKumaUrl(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return UptimeKumaClient.ParseBaseUri(value, label).AbsoluteUri;
    }
}

file static class StringExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) =>
        value.IndexOfAny(characters) >= 0;
}
