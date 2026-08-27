using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Scheduling;

namespace TrueNasAppManager.Services;

public interface IConfigurationBackupService
{
    /// <summary>Exports all portable configuration and secrets protected by a password.</summary>
    /// <param name="password">The password used to derive the backup encryption key.</param>
    /// <param name="cancellationToken">A token that cancels the export.</param>
    /// <returns>The downloadable full recovery JSON backup.</returns>
    Task<ConfigurationBackupFile> ExportFullRecoveryAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>Reads non-secret metadata from a backup without changing configuration.</summary>
    /// <param name="json">The backup JSON.</param>
    /// <returns>The backup metadata available without decryption.</returns>
    ConfigurationBackupInspection Inspect(string json);

    /// <summary>Decrypts and validates a full recovery backup without changing configuration.</summary>
    /// <param name="json">The backup JSON.</param>
    /// <param name="password">The full recovery backup password.</param>
    /// <param name="cancellationToken">A token that cancels validation.</param>
    /// <returns>A validated import preview.</returns>
    Task<ConfigurationBackupPreview> PreviewAsync(string json, string password, CancellationToken cancellationToken = default);

    /// <summary>Validates and merges a password-protected full recovery backup transactionally.</summary>
    /// <param name="json">The backup JSON.</param>
    /// <param name="password">The full recovery backup password.</param>
    /// <param name="cancellationToken">A token that cancels the import.</param>
    /// <returns>A summary of restored application settings and connection readiness.</returns>
    Task<ConfigurationRestoreResult> ImportAsync(string json, string password, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationBackupService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISecretProtector secretProtector,
    IAppLinkService appLinkService,
    IScheduleService scheduleService,
    TimeProvider timeProvider) : IConfigurationBackupService
{
    private const int SchemaVersion = 4;
    private const int MaximumBackupBytes = 2 * 1024 * 1024;
    private const int PasswordIterations = 600_000;
    private const string AdditionalData = "TrueNasAppManager:configuration-backup:v1";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <inheritdoc cref="IConfigurationBackupService.ExportFullRecoveryAsync"/>
    public async Task<ConfigurationBackupFile> ExportFullRecoveryAsync(string password, CancellationToken cancellationToken = default)
    {
        ValidateNewPassword(password);
        var now = timeProvider.GetUtcNow();
        var payload = await LoadPayloadAsync(cancellationToken);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            var encryption = Encrypt(payloadBytes, password);
            var envelope = CreateEnvelope(now, includesSecrets: true, configuration: null, encryption);
            return new ConfigurationBackupFile(CreateFileName(now), JsonSerializer.Serialize(envelope, JsonOptions), true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }

    /// <inheritdoc cref="IConfigurationBackupService.Inspect"/>
    public ConfigurationBackupInspection Inspect(string json)
    {
        var envelope = ParseEnvelope(json);
        return new ConfigurationBackupInspection(envelope.SchemaVersion, envelope.ExportedAtUtc, envelope.ApplicationVersion, envelope.IncludesSecrets, envelope.Configuration?.Apps.Count);
    }

    /// <inheritdoc cref="IConfigurationBackupService.PreviewAsync"/>
    public Task<ConfigurationBackupPreview> PreviewAsync(string json, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = ParseEnvelope(json);
        var payload = ReadPayload(envelope, password);
        ValidatePayload(payload, envelope.IncludesSecrets);
        return Task.FromResult(new ConfigurationBackupPreview(envelope.SchemaVersion, envelope.ExportedAtUtc, envelope.ApplicationVersion, envelope.IncludesSecrets, payload.Apps.Count));
    }

    /// <inheritdoc cref="IConfigurationBackupService.ImportAsync"/>
    public async Task<ConfigurationRestoreResult> ImportAsync(string json, string password, CancellationToken cancellationToken = default)
    {
        var envelope = ParseEnvelope(json);
        var payload = ReadPayload(envelope, password);
        ValidatePayload(payload, envelope.IncludesSecrets);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
        ApplySettings(settings, payload.Settings, envelope.SchemaVersion);
        ApplySecrets(settings, payload.Secrets ?? throw new InvalidOperationException("The full recovery backup does not contain its secret configuration."), envelope.SchemaVersion);

        if (string.IsNullOrWhiteSpace(settings.TrueNasUsername) || string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted))
        {
            settings.OnboardingCompleted = false;
            settings.OnboardingStep = 1;
        }

        foreach (var backupApp in payload.Apps)
        {
            var app = await db.Apps.SingleOrDefaultAsync(item => item.Id == backupApp.AppId, cancellationToken);
            if (app is null)
            {
                app = new AppRecord
                {
                    Id = backupApp.AppId,
                    Name = backupApp.Name,
                    IsInstalled = false,
                    State = "STOPPED",
                    HealthState = AppHealthState.Unknown,
                    StatusLabel = "Awaiting inventory refresh",
                    LastSeenUtc = timeProvider.GetUtcNow().UtcDateTime
                };
                db.Apps.Add(app);
            }

            ApplyAppConfiguration(app, backupApp);
            if (backupApp.UptimeKumaMonitorIds is not null)
            {
                await ApplyUptimeKumaMonitorLinksAsync(db, app.Id, backupApp.UptimeKumaMonitorIds, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var connectionReady = !string.IsNullOrWhiteSpace(settings.TrueNasUsername) && !string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted);
        return new ConfigurationRestoreResult(payload.Apps.Count, true, connectionReady);
    }

    private async Task<BackupPayload> LoadPayloadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
        var apps = await db.Apps.AsNoTracking().Include(app => app.UptimeKumaMonitors).OrderBy(app => app.Id).ToListAsync(cancellationToken);
        var appConfigurations = apps.Select(CreateAppBackup).ToList();
        var secrets = new BackupSecrets(ReadSecret(settings.TrueNasApiKeyEncrypted), ReadSecret(settings.WebhookAuthorizationEncrypted), ReadSecret(settings.WebhookHeadersEncrypted), ReadSecret(settings.UptimeKumaApiKeyEncrypted));
        return new BackupPayload(CreateSettingsBackup(settings), appConfigurations, secrets);
    }

    private BackupAppConfiguration CreateAppBackup(AppRecord app)
    {
        var localUrl = NormalizeOptionalHttpUrl(app.LocalPortalUrl, "Local Web UI URL");
        var remoteUrl = NormalizeOptionalHttpUrl(app.RemotePortalUrl, "Remote Web UI URL");
        var legacyUrl = TryNormalizeHttpUrl(app.ManualPortalUrl);
        if (legacyUrl is not null)
        {
            if (appLinkService.ClassifyRoute(new Uri(legacyUrl)) == WebUiRoute.Local)
            {
                localUrl ??= legacyUrl;
            }
            else
            {
                remoteUrl ??= legacyUrl;
            }
        }

        return new BackupAppConfiguration(app.Id, app.Name, app.Policy, app.VersionScope, app.SnapshotHostPaths, app.NotifySuccessOverride, app.DowntimeAction, app.MaintenanceMode, localUrl, remoteUrl, app.UptimeKumaMonitors.Select(monitor => monitor.MonitorId).OrderBy(id => id, StringComparer.Ordinal).ToList(), app.IsFavorite, app.GroupName);
    }

    private static BackupSettings CreateSettingsBackup(SettingsRecord settings) => new(
        settings.OnboardingCompleted,
        settings.TrueNasUsername,
        settings.VerifyTls,
        settings.SchedulerEnabled,
        settings.CronExpression,
        settings.TimeZoneId,
        settings.NotifyManualApproval,
        settings.NotifyAutomaticFailure,
        settings.NotifyAutomaticBlocked,
        settings.NotifyRollback,
        settings.NotifyAutomaticSuccess,
        settings.NotifyScheduledCheckFailure,
        settings.NotifyConnectionFailure,
        settings.EmailEnabled,
        DeserializeStringList(settings.EmailRecipientsJson),
        settings.PortalHostOverride,
        settings.GitHubEnrichmentEnabled,
        settings.WebhookEnabled,
        settings.WebhookUrl,
        settings.WebhookTimeoutSeconds,
        settings.VerificationTimeoutSeconds,
        settings.ConnectionFailureCooldownMinutes,
        settings.HistoryRetentionDays,
        settings.ManagerAppId,
        !string.IsNullOrWhiteSpace(settings.UptimeKumaBaseUrl),
        settings.UptimeKumaBaseUrl,
        settings.UptimeKumaBrowserUrl,
        settings.UptimeKumaVerifyTls,
        settings.UptimeKumaRefreshIntervalSeconds,
        settings.OnboardingStep);

    private void ValidatePayload(BackupPayload payload, bool includesSecrets)
    {
        if (payload.Settings is null || payload.Apps is null || payload.Settings.EmailRecipients is null)
        {
            throw new InvalidOperationException("The backup configuration is incomplete.");
        }

        if (includesSecrets != (payload.Secrets is not null))
        {
            throw new InvalidOperationException("The backup secret payload does not match its declared mode.");
        }

        if (payload.Settings.SchedulerEnabled)
        {
            var schedule = scheduleService.Validate(payload.Settings.CronExpression, payload.Settings.TimeZoneId);
            if (!schedule.IsValid)
            {
                throw new InvalidOperationException($"The backup contains an invalid schedule: {schedule.Error}");
            }
        }

        foreach (var recipient in payload.Settings.EmailRecipients)
        {
            try
            {
                _ = new MailAddress(recipient);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"The backup contains an invalid email recipient: '{recipient}'.");
            }
        }

        _ = NormalizePortalHost(payload.Settings.PortalHostOverride);
        _ = NormalizeOptionalHttpUrl(payload.Settings.WebhookUrl, "Webhook URL");
        _ = NormalizeOptionalHttpUrl(payload.Settings.UptimeKumaBaseUrl, "Uptime Kuma connection URL");
        _ = NormalizeOptionalHttpUrl(payload.Settings.UptimeKumaBrowserUrl, "Uptime Kuma browser URL");
        if (payload.Settings.WebhookEnabled && string.IsNullOrWhiteSpace(payload.Settings.WebhookUrl))
        {
            throw new InvalidOperationException("The backup enables the webhook without providing a URL.");
        }

        if (payload.Settings.WebhookTimeoutSeconds is < 1 or > 120 ||
            payload.Settings.VerificationTimeoutSeconds is < 30 or > 1800 ||
            payload.Settings.ConnectionFailureCooldownMinutes is < 1 or > 10080 ||
            payload.Settings.HistoryRetentionDays is < 1 ||
            payload.Settings.UptimeKumaRefreshIntervalSeconds is < 30 or > 3600 ||
            payload.Settings.OnboardingStep is < 1 or > 4)
        {
            throw new InvalidOperationException("The backup contains one or more advanced settings outside the allowed range.");
        }

        var duplicateApp = payload.Apps.GroupBy(app => app.AppId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateApp is not null)
        {
            throw new InvalidOperationException($"The backup contains duplicate settings for app '{duplicateApp.Key}'.");
        }

        foreach (var app in payload.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.AppId) || app.AppId.Length > 256 || string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 256)
            {
                throw new InvalidOperationException("Every backed-up application must have a valid ID and name.");
            }

            _ = NormalizeOptionalHttpUrl(app.LocalPortalUrl, $"Local Web UI URL for {app.AppId}");
            _ = NormalizeOptionalHttpUrl(app.RemotePortalUrl, $"Remote Web UI URL for {app.AppId}");
            _ = NormalizeGroupName(app.GroupName, app.AppId);
            if (app.UptimeKumaMonitorIds?.Any(string.IsNullOrWhiteSpace) == true || app.UptimeKumaMonitorIds?.Any(id => id.Length > 128) == true || app.UptimeKumaMonitorIds?.Distinct(StringComparer.Ordinal).Count() != app.UptimeKumaMonitorIds?.Count)
            {
                throw new InvalidOperationException($"The backup contains invalid or duplicate Uptime Kuma monitor IDs for app '{app.AppId}'.");
            }
        }

        var duplicateMonitor = payload.Apps
            .SelectMany(app => app.UptimeKumaMonitorIds ?? [])
            .GroupBy(monitorId => monitorId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMonitor is not null)
        {
            throw new InvalidOperationException($"The backup links Uptime Kuma monitor '{duplicateMonitor.Key}' to more than one app.");
        }
    }

    private static void ApplySettings(SettingsRecord target, BackupSettings source, int schemaVersion)
    {
        target.OnboardingCompleted = source.OnboardingCompleted;
        target.OnboardingStep = source.OnboardingStep ?? (source.OnboardingCompleted ? 4 : 1);
        target.TrueNasUsername = NullIfWhiteSpace(source.TrueNasUsername);
        target.VerifyTls = source.VerifyTls;
        target.AllowInsecureWebSocket = false;
        target.SchedulerEnabled = source.SchedulerEnabled;
        target.CronExpression = NullIfWhiteSpace(source.CronExpression);
        target.TimeZoneId = NullIfWhiteSpace(source.TimeZoneId);
        target.NotifyManualApproval = source.NotifyManualApproval;
        target.NotifyAutomaticFailure = source.NotifyAutomaticFailure;
        target.NotifyAutomaticBlocked = source.NotifyAutomaticBlocked;
        target.NotifyRollback = source.NotifyRollback;
        target.NotifyAutomaticSuccess = source.NotifyAutomaticSuccess;
        target.NotifyScheduledCheckFailure = source.NotifyScheduledCheckFailure;
        target.NotifyConnectionFailure = source.NotifyConnectionFailure;
        target.EmailEnabled = source.EmailEnabled;
        target.EmailRecipientsJson = JsonSerializer.Serialize(source.EmailRecipients.Distinct(StringComparer.OrdinalIgnoreCase), JsonOptions);
        target.PortalHostOverride = NormalizePortalHost(source.PortalHostOverride);
        target.GitHubEnrichmentEnabled = source.GitHubEnrichmentEnabled;
        target.WebhookEnabled = source.WebhookEnabled;
        target.WebhookUrl = NormalizeOptionalHttpUrl(source.WebhookUrl, "Webhook URL");
        target.WebhookTimeoutSeconds = source.WebhookTimeoutSeconds;
        target.VerificationTimeoutSeconds = source.VerificationTimeoutSeconds;
        target.ConnectionFailureCooldownMinutes = source.ConnectionFailureCooldownMinutes;
        target.HistoryRetentionDays = source.HistoryRetentionDays;
        target.ManagerAppId = NullIfWhiteSpace(source.ManagerAppId);
        if (schemaVersion >= 2)
        {
            target.UptimeKumaBaseUrl = NormalizeOptionalHttpUrl(source.UptimeKumaBaseUrl, "Uptime Kuma connection URL");
            target.UptimeKumaEnabled = target.UptimeKumaBaseUrl is not null;
            target.UptimeKumaBrowserUrl = NormalizeOptionalHttpUrl(source.UptimeKumaBrowserUrl, "Uptime Kuma browser URL");
            target.UptimeKumaVerifyTls = source.UptimeKumaVerifyTls ?? true;
            target.UptimeKumaRefreshIntervalSeconds = source.UptimeKumaRefreshIntervalSeconds ?? 60;
        }
    }

    private void ApplySecrets(SettingsRecord target, BackupSecrets secrets, int schemaVersion)
    {
        target.TrueNasApiKeyEncrypted = ProtectOptional(secrets.TrueNasApiKey);
        target.WebhookAuthorizationEncrypted = ProtectOptional(secrets.WebhookAuthorization);
        target.WebhookHeadersEncrypted = ProtectOptional(secrets.WebhookHeaders);
        if (schemaVersion >= 2)
        {
            target.UptimeKumaApiKeyEncrypted = ProtectOptional(secrets.UptimeKumaApiKey);
        }
    }

    private static void ApplyAppConfiguration(AppRecord target, BackupAppConfiguration source)
    {
        if (!target.IsInstalled || string.IsNullOrWhiteSpace(target.Name))
        {
            target.Name = source.Name.Trim();
        }
        target.Policy = source.Policy;
        target.VersionScope = source.VersionScope;
        target.SnapshotHostPaths = source.SnapshotHostPaths;
        target.NotifySuccessOverride = source.NotifySuccessOverride;
        target.DowntimeAction = source.DowntimeAction;
        target.NotifyOnDowntime = source.DowntimeAction != DowntimeAction.Ignore;
        target.MaintenanceMode = source.MaintenanceMode;
        target.LocalPortalUrl = NormalizeOptionalHttpUrl(source.LocalPortalUrl, $"Local Web UI URL for {source.AppId}");
        target.RemotePortalUrl = NormalizeOptionalHttpUrl(source.RemotePortalUrl, $"Remote Web UI URL for {source.AppId}");
        target.ManualPortalUrl = null;
        if (source.IsFavorite is not null)
        {
            target.IsFavorite = source.IsFavorite.Value;
            target.GroupName = NormalizeGroupName(source.GroupName, source.AppId);
        }
        if (source.DowntimeAction == DowntimeAction.Ignore)
        {
            target.DowntimeNotificationActive = false;
            target.HealthIncidentId = null;
            target.RecoveryAttemptedUtc = null;
        }
    }

    private async Task ApplyUptimeKumaMonitorLinksAsync(AppDbContext db, string appId, IReadOnlyCollection<string> monitorIds, CancellationToken cancellationToken)
    {
        var linked = await db.UptimeKumaMonitors.Where(monitor => monitor.AppId == appId).ToListAsync(cancellationToken);
        foreach (var monitor in linked)
        {
            monitor.AppId = null;
        }

        foreach (var monitorId in monitorIds.Distinct(StringComparer.Ordinal))
        {
            var monitor = await db.UptimeKumaMonitors.SingleOrDefaultAsync(item => item.MonitorId == monitorId, cancellationToken);
            if (monitor is null)
            {
                monitor = new UptimeKumaMonitorRecord
                {
                    MonitorId = monitorId,
                    Name = monitorId,
                    Type = "unknown",
                    Status = UptimeKumaMonitorStatus.Unknown,
                    IsPresent = false,
                    LastSeenUtc = timeProvider.GetUtcNow().UtcDateTime
                };
                db.UptimeKumaMonitors.Add(monitor);
            }

            monitor.AppId = appId;
        }
    }

    private static BackupEnvelope CreateEnvelope(DateTimeOffset now, bool includesSecrets, BackupPayload? configuration, BackupEncryption? encryption) => new()
    {
        SchemaVersion = SchemaVersion,
        ExportedAtUtc = now,
        ApplicationVersion = GetApplicationVersion(),
        IncludesSecrets = includesSecrets,
        Configuration = configuration,
        Encryption = encryption
    };

    private static BackupEnvelope ParseEnvelope(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Select a non-empty JSON backup file.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumBackupBytes)
        {
            throw new InvalidOperationException("The backup exceeds the 2 MB import limit.");
        }

        BackupEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BackupEnvelope>(json, JsonOptions) ?? throw new JsonException("The document was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The selected file is not a valid TrueNAS App Manager backup.", exception);
        }

        if (envelope.SchemaVersion is < 1 or > SchemaVersion)
        {
            throw new InvalidOperationException($"Backup schema {envelope.SchemaVersion} is not supported. This version accepts schemas 1 through {SchemaVersion}.");
        }

        if (envelope.ExportedAtUtc == default || string.IsNullOrWhiteSpace(envelope.ApplicationVersion))
        {
            throw new InvalidOperationException("The backup metadata is incomplete.");
        }

        if (envelope.IncludesSecrets == (envelope.Encryption is null) || envelope.IncludesSecrets == (envelope.Configuration is not null))
        {
            throw new InvalidOperationException("The backup payload does not match its secret mode.");
        }

        if (!envelope.IncludesSecrets || envelope.Encryption is null)
        {
            throw new InvalidOperationException("Only a password-protected full recovery backup can be restored. Secret-free backups are not accepted.");
        }

        return envelope;
    }

    private static BackupPayload ReadPayload(BackupEnvelope envelope, string password)
    {
        ValidatePassword(password);
        var plaintext = Decrypt(envelope.Encryption ?? throw new InvalidOperationException("The full recovery backup is incomplete."), password);
        try
        {
            return JsonSerializer.Deserialize<BackupPayload>(plaintext, JsonOptions) ?? throw new InvalidOperationException("The encrypted backup payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The encrypted backup payload is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static BackupEncryption Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var key = new byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, key, PasswordIterations, HashAlgorithmName.SHA256);
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(AdditionalData));
            return new BackupEncryption("PBKDF2-SHA256", PasswordIterations, Convert.ToBase64String(salt), "AES-256-GCM", Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Decrypt(BackupEncryption encryption, string password)
    {
        if (!string.Equals(encryption.Kdf, "PBKDF2-SHA256", StringComparison.Ordinal) ||
            encryption.Iterations != PasswordIterations ||
            !string.Equals(encryption.Cipher, "AES-256-GCM", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backup uses unsupported encryption settings.");
        }

        try
        {
            var salt = Convert.FromBase64String(encryption.Salt);
            var nonce = Convert.FromBase64String(encryption.Nonce);
            var tag = Convert.FromBase64String(encryption.Tag);
            var ciphertext = Convert.FromBase64String(encryption.Ciphertext);
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16)
            {
                throw new InvalidOperationException("The encrypted backup parameters are invalid.");
            }

            var plaintext = new byte[ciphertext.Length];
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var key = new byte[32];
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, key, PasswordIterations, HashAlgorithmName.SHA256);
                using var aes = new AesGcm(key, tag.Length);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(AdditionalData));
                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("The backup password is incorrect or the encrypted file has been changed.", exception);
        }
    }

    private string? ReadSecret(string? encrypted) => string.IsNullOrWhiteSpace(encrypted) ? null : secretProtector.Unprotect(encrypted);

    private string? ProtectOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : secretProtector.Protect(value);

    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Enter the full recovery backup password.");
        }
    }

    private static void ValidateNewPassword(string? password)
    {
        ValidatePassword(password);
        if (password!.Length < 12)
        {
            throw new InvalidOperationException("Use at least 12 characters for the full recovery backup password.");
        }
    }

    private static string? NormalizeOptionalHttpUrl(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = TryNormalizeHttpUrl(value);
        if (normalized is null)
        {
            throw new InvalidOperationException($"{label} must be an absolute HTTP or HTTPS URL without embedded credentials.");
        }

        return normalized;
    }

    private static string? TryNormalizeHttpUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static string? NormalizePortalHost(string? value)
    {
        var normalized = NormalizeOptionalHttpUrl(value, "Local TrueNAS host override");
        if (normalized is null)
        {
            return null;
        }

        var uri = new Uri(normalized);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Local TrueNAS host override cannot contain a query or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeGroupName(string? value, string appId)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized?.Length > 64)
        {
            throw new InvalidOperationException($"The group name for app '{appId}' cannot exceed 64 characters.");
        }

        return normalized;
    }

    private static List<string> DeserializeStringList(string? value)
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

    private static string CreateFileName(DateTimeOffset now) => $"truenas-app-manager-full-recovery-{now.UtcDateTime:yyyyMMddTHHmmssZ}.json";

    private static string GetApplicationVersion() => ApplicationVersion.Current;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed record BackupEnvelope
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset ExportedAtUtc { get; init; }
    public string ApplicationVersion { get; init; } = string.Empty;
    public bool IncludesSecrets { get; init; }
    public BackupPayload? Configuration { get; init; }
    public BackupEncryption? Encryption { get; init; }
}

internal sealed record BackupPayload(BackupSettings Settings, List<BackupAppConfiguration> Apps, BackupSecrets? Secrets);

internal sealed record BackupSettings(
    bool OnboardingCompleted,
    string? TrueNasUsername,
    bool VerifyTls,
    bool SchedulerEnabled,
    string? CronExpression,
    string? TimeZoneId,
    bool NotifyManualApproval,
    bool NotifyAutomaticFailure,
    bool NotifyAutomaticBlocked,
    bool NotifyRollback,
    bool NotifyAutomaticSuccess,
    bool NotifyScheduledCheckFailure,
    bool NotifyConnectionFailure,
    bool EmailEnabled,
    List<string> EmailRecipients,
    string? PortalHostOverride,
    bool GitHubEnrichmentEnabled,
    bool WebhookEnabled,
    string? WebhookUrl,
    int WebhookTimeoutSeconds,
    int VerificationTimeoutSeconds,
    int ConnectionFailureCooldownMinutes,
    int? HistoryRetentionDays,
    string? ManagerAppId,
    bool? UptimeKumaEnabled = null,
    string? UptimeKumaBaseUrl = null,
    string? UptimeKumaBrowserUrl = null,
    bool? UptimeKumaVerifyTls = null,
    int? UptimeKumaRefreshIntervalSeconds = null,
    int? OnboardingStep = null);

internal sealed record BackupAppConfiguration(
    string AppId,
    string Name,
    AppPolicy? Policy,
    VersionScope VersionScope,
    bool SnapshotHostPaths,
    bool? NotifySuccessOverride,
    DowntimeAction DowntimeAction,
    bool MaintenanceMode,
    string? LocalPortalUrl,
    string? RemotePortalUrl,
    List<string>? UptimeKumaMonitorIds = null,
    bool? IsFavorite = null,
    string? GroupName = null);

internal sealed record BackupSecrets(string? TrueNasApiKey, string? WebhookAuthorization, string? WebhookHeaders, string? UptimeKumaApiKey = null);

internal sealed record BackupEncryption(string Kdf, int Iterations, string Salt, string Cipher, string Nonce, string Tag, string Ciphertext);
