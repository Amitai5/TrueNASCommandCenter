using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Notifications;
using TrueNasAppManager.Scheduling;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class ConfigurationBackupServiceTests
{
    private static readonly DateTimeOffset BackupTime = new(2026, 8, 23, 1, 30, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullRecoveryBackup_RoundTripRestoresConfigurationSecretsAndHistory()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        await SeedAppsAndHistoryAsync(database);
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        Assert.DoesNotContain("original-key", backup.Json);
        Assert.DoesNotContain("Bearer original", backup.Json);

        await using (var mutate = await database.CreateDbContextAsync())
        {
            var settings = await mutate.Settings.SingleAsync();
            settings.SchedulerEnabled = false;
            settings.TrueNasApiKeyEncrypted = protector.Protect("replacement-key");
            settings.UptimeKumaApiKeyEncrypted = protector.Protect("kuma-replacement");
            var plex = await mutate.Apps.SingleAsync(app => app.Id == "plex");
            plex.Name = "Plex current";
            plex.Policy = AppPolicy.Ignore;
            plex.IsFavorite = false;
            plex.GroupName = null;
            (await mutate.UptimeKumaMonitors.SingleAsync()).AppId = null;
            mutate.Apps.Add(CreateConfiguredApp("unlisted", AppPolicy.NotifyOnly));
            await mutate.SaveChangesAsync();
        }

        var result = await service.ImportAsync(backup.Json, "correct horse battery staple");

        Assert.AreEqual(1, result.AppsRestored);
        Assert.IsTrue(result.SecretsRestored);
        await using var verify = await database.CreateDbContextAsync();
        var restoredSettings = await verify.Settings.SingleAsync();
        Assert.IsTrue(restoredSettings.SchedulerEnabled);
        Assert.AreEqual("original-key", protector.Unprotect(restoredSettings.TrueNasApiKeyEncrypted!));
        Assert.IsTrue(restoredSettings.UptimeKumaEnabled);
        Assert.AreEqual("http://kuma.local:3001/", restoredSettings.UptimeKumaBaseUrl);
        Assert.AreEqual("kuma-original", protector.Unprotect(restoredSettings.UptimeKumaApiKeyEncrypted!));
        var restoredPlex = await verify.Apps.SingleAsync(app => app.Id == "plex");
        Assert.AreEqual("Plex current", restoredPlex.Name);
        Assert.AreEqual(AppPolicy.AutoUpdate, restoredPlex.Policy);
        Assert.IsTrue(restoredPlex.IsFavorite);
        Assert.AreEqual("Media", restoredPlex.GroupName);
        Assert.AreEqual("plex", (await verify.UptimeKumaMonitors.SingleAsync()).AppId);
        Assert.AreEqual(AppPolicy.NotifyOnly, (await verify.Apps.SingleAsync(app => app.Id == "unlisted")).Policy);
        Assert.AreEqual(1, await verify.UpdateRuns.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullRecoveryBackup_CorrectPasswordRestoresSecretsAndWrongPasswordDoesNotMutate()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        await SeedAppsAndHistoryAsync(database);
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        Assert.DoesNotContain("original-key", backup.Json);
        Assert.DoesNotContain("Bearer original", backup.Json);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(backup.Json, "wrong password"));
        await using (var unchanged = await database.CreateDbContextAsync())
        {
            Assert.AreEqual("original-key", protector.Unprotect((await unchanged.Settings.SingleAsync()).TrueNasApiKeyEncrypted!));
        }

        await using (var mutate = await database.CreateDbContextAsync())
        {
            var settings = await mutate.Settings.SingleAsync();
            settings.TrueNasApiKeyEncrypted = protector.Protect("replacement-key");
            settings.WebhookAuthorizationEncrypted = null;
            settings.UptimeKumaApiKeyEncrypted = protector.Protect("kuma-replacement");
            await mutate.SaveChangesAsync();
        }

        var result = await service.ImportAsync(backup.Json, "correct horse battery staple");

        Assert.IsTrue(result.SecretsRestored);
        await using var verify = await database.CreateDbContextAsync();
        var restored = await verify.Settings.SingleAsync();
        Assert.AreEqual("original-key", protector.Unprotect(restored.TrueNasApiKeyEncrypted!));
        Assert.AreEqual("Bearer original", protector.Unprotect(restored.WebhookAuthorizationEncrypted!));
        Assert.AreEqual("kuma-original", protector.Unprotect(restored.UptimeKumaApiKeyEncrypted!));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullRecoveryBackup_FreshInstallationRestoresEveryPortableConfigurationField()
    {
        await using var sourceDatabase = new TestDatabase();
        var sourceProtector = sourceDatabase.CreateProtector();
        await sourceDatabase.InitializeAsync(settings => ConfigureSettings(settings, sourceProtector, "source-key"));
        await SeedAppsAndHistoryAsync(sourceDatabase);
        var sourceService = CreateService(sourceDatabase, sourceProtector);

        var backup = await sourceService.ExportFullRecoveryAsync("correct horse battery staple");

        Assert.IsTrue(backup.IncludesSecrets);
        StringAssert.Contains(backup.FileName, "full-recovery");
        Assert.DoesNotContain("source-key", backup.Json);
        Assert.DoesNotContain("kuma-original", backup.Json);
        Assert.DoesNotContain("Bearer original", backup.Json);

        await using var targetDatabase = new TestDatabase();
        var targetProtector = targetDatabase.CreateProtector();
        await targetDatabase.InitializeAsync();
        var targetService = CreateService(targetDatabase, targetProtector);

        var result = await targetService.ImportAsync(backup.Json, "correct horse battery staple");

        Assert.AreEqual(1, result.AppsRestored);
        Assert.IsTrue(result.SecretsRestored);
        Assert.IsTrue(result.ConnectionReady);
        await using var verify = await targetDatabase.CreateDbContextAsync();
        var settings = await verify.Settings.SingleAsync();
        Assert.IsTrue(settings.OnboardingCompleted);
        Assert.AreEqual(4, settings.OnboardingStep);
        Assert.AreEqual("autoupdate", settings.TrueNasUsername);
        Assert.AreEqual("source-key", targetProtector.Unprotect(settings.TrueNasApiKeyEncrypted!));
        Assert.IsFalse(settings.VerifyTls);
        Assert.IsTrue(settings.SchedulerEnabled);
        Assert.AreEqual("0 23 * * 6", settings.CronExpression);
        Assert.AreEqual("America/Los_Angeles", settings.TimeZoneId);
        Assert.IsTrue(settings.NotifyManualApproval);
        Assert.IsTrue(settings.NotifyAutomaticFailure);
        Assert.IsFalse(settings.NotifyAutomaticBlocked);
        Assert.IsTrue(settings.NotifyRollback);
        Assert.IsFalse(settings.NotifyAutomaticSuccess);
        Assert.IsTrue(settings.NotifyScheduledCheckFailure);
        Assert.IsFalse(settings.NotifyConnectionFailure);
        Assert.IsTrue(settings.EmailEnabled);
        var recipients = JsonSerializer.Deserialize<List<string>>(settings.EmailRecipientsJson!);
        Assert.IsNotNull(recipients);
        CollectionAssert.AreEqual(new[] { "admin@example.test", "ops@example.test" }, recipients);
        Assert.AreEqual("http://truenas.local", settings.PortalHostOverride);
        Assert.IsFalse(settings.GitHubEnrichmentEnabled);
        Assert.IsTrue(settings.WebhookEnabled);
        Assert.AreEqual("https://hooks.example.test/truenas", settings.WebhookUrl);
        Assert.AreEqual("Bearer original", targetProtector.Unprotect(settings.WebhookAuthorizationEncrypted!));
        Assert.AreEqual("X-Test: original", targetProtector.Unprotect(settings.WebhookHeadersEncrypted!));
        Assert.AreEqual(37, settings.WebhookTimeoutSeconds);
        Assert.AreEqual(420, settings.VerificationTimeoutSeconds);
        Assert.AreEqual(720, settings.ConnectionFailureCooldownMinutes);
        Assert.AreEqual(45, settings.HistoryRetentionDays);
        Assert.AreEqual("truenas-app-manager", settings.ManagerAppId);
        Assert.IsTrue(settings.UptimeKumaEnabled);
        Assert.AreEqual("http://kuma.local:3001/", settings.UptimeKumaBaseUrl);
        Assert.AreEqual("https://status.example.test/", settings.UptimeKumaBrowserUrl);
        Assert.AreEqual("kuma-original", targetProtector.Unprotect(settings.UptimeKumaApiKeyEncrypted!));
        Assert.IsFalse(settings.UptimeKumaVerifyTls);
        Assert.AreEqual(90, settings.UptimeKumaRefreshIntervalSeconds);

        var app = await verify.Apps.Include(item => item.UptimeKumaMonitors).SingleAsync();
        Assert.AreEqual("plex", app.Id);
        Assert.IsFalse(app.IsInstalled);
        Assert.AreEqual(AppPolicy.AutoUpdate, app.Policy);
        Assert.AreEqual(VersionScope.MinorAndPatch, app.VersionScope);
        Assert.IsTrue(app.SnapshotHostPaths);
        Assert.IsFalse(app.NotifySuccessOverride ?? true);
        Assert.AreEqual(DowntimeAction.NotifyOnly, app.DowntimeAction);
        Assert.IsTrue(app.NotifyOnDowntime);
        Assert.IsTrue(app.MaintenanceMode);
        Assert.IsTrue(app.IsFavorite);
        Assert.AreEqual("Media", app.GroupName);
        Assert.AreEqual("http://truenas.local/plex", app.LocalPortalUrl);
        Assert.AreEqual("https://plex.example.test/", app.RemotePortalUrl);
        Assert.HasCount(1, app.UptimeKumaMonitors);
        Assert.AreEqual("7", app.UptimeKumaMonitors.Single().MonitorId);
        Assert.AreEqual(0, await verify.UpdateRuns.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullRecoveryBackup_TamperedCiphertextIsRejected()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        document["encryption"]!["ciphertext"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(document.ToJsonString(), "correct horse battery staple"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ExportFullRecoveryAsync_PasswordShorterThanTwelveCharacters_IsRejected()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportFullRecoveryAsync("short"));

        StringAssert.Contains(exception.Message, "at least 12 characters");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PreviewAsync_FullRecoveryWithoutPassword_IsRejected()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(backup.Json, string.Empty));

        StringAssert.Contains(exception.Message, "full recovery backup password");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ImportAsync_FullRecoveryWithoutPassword_IsRejectedWithoutMutation()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(backup.Json, string.Empty));

        await using var verify = await database.CreateDbContextAsync();
        var settings = await verify.Settings.SingleAsync();
        Assert.AreEqual("original-key", protector.Unprotect(settings.TrueNasApiKeyEncrypted!));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Inspect_SecretFreeBackup_IsRejected()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var secretFreeBackup = ConvertToSecretFreeBackup(backup.Json, "correct horse battery staple");

        var exception = Assert.Throws<InvalidOperationException>(() => service.Inspect(secretFreeBackup));

        StringAssert.Contains(exception.Message, "Only a password-protected full recovery backup");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ImportAsync_SecretFreeBackup_IsRejectedWithoutMutation()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var secretFreeBackup = ConvertToSecretFreeBackup(backup.Json, "correct horse battery staple");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(secretFreeBackup, "correct horse battery staple"));

        await using var verify = await database.CreateDbContextAsync();
        var settings = await verify.Settings.SingleAsync();
        Assert.AreEqual("original-key", protector.Unprotect(settings.TrueNasApiKeyEncrypted!));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullRecoveryBackup_ImportCreatesPlaceholderAndPreservesUnlistedApp()
    {
        await using var sourceDatabase = new TestDatabase();
        var sourceProtector = sourceDatabase.CreateProtector();
        await sourceDatabase.InitializeAsync(settings => ConfigureSettings(settings, sourceProtector, "source-key"));
        await using (var source = await sourceDatabase.CreateDbContextAsync())
        {
            source.Apps.Add(CreateConfiguredApp("missing", AppPolicy.AutoUpdate));
            await source.SaveChangesAsync();
        }
        var sourceService = CreateService(sourceDatabase, sourceProtector);
        var backup = await sourceService.ExportFullRecoveryAsync("correct horse battery staple");

        await using var targetDatabase = new TestDatabase();
        var targetProtector = targetDatabase.CreateProtector();
        await targetDatabase.InitializeAsync(settings => ConfigureSettings(settings, targetProtector, "target-key"));
        await using (var target = await targetDatabase.CreateDbContextAsync())
        {
            target.Apps.Add(CreateConfiguredApp("unlisted", AppPolicy.NotifyOnly));
            await target.SaveChangesAsync();
        }
        var targetService = CreateService(targetDatabase, targetProtector);

        var result = await targetService.ImportAsync(backup.Json, "correct horse battery staple");

        Assert.AreEqual(1, result.AppsRestored);
        await using var verify = await targetDatabase.CreateDbContextAsync();
        var placeholder = await verify.Apps.SingleAsync(app => app.Id == "missing");
        Assert.IsFalse(placeholder.IsInstalled);
        Assert.AreEqual(AppPolicy.AutoUpdate, placeholder.Policy);
        Assert.AreEqual("https://missing.example.test/", placeholder.RemotePortalUrl);
        Assert.IsTrue(placeholder.IsFavorite);
        Assert.AreEqual("Media", placeholder.GroupName);
        Assert.AreEqual(AppPolicy.NotifyOnly, (await verify.Apps.SingleAsync(app => app.Id == "unlisted")).Policy);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Import_InvalidAppUrlRejectsWholeBackupWithoutMutation()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        await SeedAppsAndHistoryAsync(database);
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var invalidBackup = MutateFullRecoveryPayload(backup.Json, "correct horse battery staple", payload =>
        {
            payload["settings"]!["schedulerEnabled"] = false;
            payload["apps"]![0]!["remotePortalUrl"] = "javascript:alert(1)";
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(invalidBackup, "correct horse battery staple"));

        await using var verify = await database.CreateDbContextAsync();
        Assert.IsTrue((await verify.Settings.SingleAsync()).SchedulerEnabled);
        Assert.AreEqual(AppPolicy.AutoUpdate, (await verify.Apps.SingleAsync(app => app.Id == "plex")).Policy);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Preview_MonitorLinkedToMultipleAppsRejectsBackup()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        await SeedAppsAndHistoryAsync(database);
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var invalidBackup = MutateFullRecoveryPayload(backup.Json, "correct horse battery staple", payload =>
        {
            var apps = payload["apps"]!.AsArray();
            var duplicate = apps[0]!.DeepClone();
            duplicate["appId"] = "duplicate-app";
            duplicate["name"] = "Duplicate app";
            apps.Add(duplicate);
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(invalidBackup, "correct horse battery staple"));

        StringAssert.Contains(exception.Message, "more than one app");
    }

    [TestMethod]
    [DataRow(99)]
    [DataRow(0)]
    [TestCategory("Unit")]
    public async Task Preview_UnsupportedSchemaIsRejected(int schemaVersion)
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        document["schemaVersion"] = schemaVersion;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(document.ToJsonString(), "correct horse battery staple"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Preview_BackupOverTwoMegabytesIsRejected()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var oversized = new string('x', 2 * 1024 * 1024 + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(oversized, "correct horse battery staple"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Import_SchemaThreeWithoutOnboardingStepRestoresCompletedWizardState()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync("correct horse battery staple");
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        document["schemaVersion"] = 3;
        var schemaThreeBackup = MutateFullRecoveryPayload(document.ToJsonString(), "correct horse battery staple", payload => payload["settings"]!.AsObject().Remove("onboardingStep"));

        await using (var mutate = await database.CreateDbContextAsync())
        {
            var settings = await mutate.Settings.SingleAsync();
            settings.OnboardingCompleted = false;
            settings.OnboardingStep = 1;
            await mutate.SaveChangesAsync();
        }

        await service.ImportAsync(schemaThreeBackup, "correct horse battery staple");

        await using var verify = await database.CreateDbContextAsync();
        var restored = await verify.Settings.SingleAsync();
        Assert.IsTrue(restored.OnboardingCompleted);
        Assert.AreEqual(4, restored.OnboardingStep);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullRecoveryBackup_RestoresVapidIdentityAndBrowserSubscriptions()
    {
        const string password = "correct horse battery staple";
        await using var source = new TestDatabase();
        var sourceProtector = source.CreateProtector();
        await source.InitializeAsync(settings => ConfigureSettings(settings, sourceProtector, "source-key"));
        var sourcePush = new WebPushSubscriptionService(source, sourceProtector, new FixedTimeProvider(BackupTime));
        await sourcePush.RegisterAsync(CreatePushSubscription());
        var originalDelivery = await sourcePush.GetDeliveryConfigurationAsync();
        var backup = await CreateService(source, sourceProtector).ExportFullRecoveryAsync(password);

        await using var target = new TestDatabase();
        var targetProtector = target.CreateProtector();
        await target.InitializeAsync();
        await CreateService(target, targetProtector).ImportAsync(backup.Json, password);

        var restoredPush = new WebPushSubscriptionService(target, targetProtector, new FixedTimeProvider(BackupTime));
        var restoredDelivery = await restoredPush.GetDeliveryConfigurationAsync();
        var devices = await restoredPush.ListAsync();
        Assert.AreEqual(originalDelivery.PublicKey, restoredDelivery.PublicKey);
        Assert.AreEqual(originalDelivery.PrivateKey, restoredDelivery.PrivateKey);
        Assert.HasCount(1, devices);
        Assert.AreEqual("Recovery phone", devices[0].DeviceName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Preview_SchemaFiveDuplicatePushSubscriptionId_IsRejected()
    {
        const string password = "correct horse battery staple";
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "source-key"));
        var push = new WebPushSubscriptionService(database, protector, new FixedTimeProvider(BackupTime));
        await push.RegisterAsync(CreatePushSubscription());
        var service = CreateService(database, protector);
        var backup = await service.ExportFullRecoveryAsync(password);
        var duplicated = MutateFullRecoveryPayload(backup.Json, password, payload =>
        {
            var subscriptions = payload["pushSubscriptions"]!.AsArray();
            var duplicate = subscriptions[0]!.DeepClone().AsObject();
            duplicate["endpoint"] = "https://push.example.test/send/second-device";
            subscriptions.Add(duplicate);
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(duplicated, password));

        StringAssert.Contains(exception.Message, "duplicate browser push subscription IDs");
    }

    private static string ConvertToSecretFreeBackup(string json, string password)
    {
        var envelope = JsonNode.Parse(json)!.AsObject();
        var payload = DecryptPayload(envelope, password);
        payload["secrets"] = null;
        envelope["includesSecrets"] = false;
        envelope["configuration"] = payload;
        envelope["encryption"] = null;
        return envelope.ToJsonString();
    }

    private static string MutateFullRecoveryPayload(string json, string password, Action<JsonObject> mutation)
    {
        var envelope = JsonNode.Parse(json)!.AsObject();
        var payload = DecryptPayload(envelope, password);
        mutation(payload);
        EncryptPayload(envelope, payload, password);
        return envelope.ToJsonString();
    }

    private static JsonObject DecryptPayload(JsonObject envelope, string password)
    {
        const string additionalData = "TrueNasAppManager:configuration-backup:v1";
        var encryption = envelope["encryption"]!.AsObject();
        var salt = Convert.FromBase64String(encryption["salt"]!.GetValue<string>());
        var nonce = Convert.FromBase64String(encryption["nonce"]!.GetValue<string>());
        var tag = Convert.FromBase64String(encryption["tag"]!.GetValue<string>());
        var ciphertext = Convert.FromBase64String(encryption["ciphertext"]!.GetValue<string>());
        var key = new byte[32];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, salt, key, 600_000, HashAlgorithmName.SHA256);
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(additionalData));
            return JsonNode.Parse(plaintext)!.AsObject();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void EncryptPayload(JsonObject envelope, JsonObject payload, string password)
    {
        const string additionalData = "TrueNasAppManager:configuration-backup:v1";
        var salt = Enumerable.Range(1, 16).Select(Convert.ToByte).ToArray();
        var nonce = Enumerable.Range(101, 12).Select(Convert.ToByte).ToArray();
        var plaintext = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var key = new byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, salt, key, 600_000, HashAlgorithmName.SHA256);
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(additionalData));
            envelope["encryption"] = new JsonObject
            {
                ["kdf"] = "PBKDF2-SHA256",
                ["iterations"] = 600_000,
                ["salt"] = Convert.ToBase64String(salt),
                ["cipher"] = "AES-256-GCM",
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
                ["ciphertext"] = Convert.ToBase64String(ciphertext)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static ConfigurationBackupService CreateService(TestDatabase database, ISecretProtector protector)
    {
        var timeProvider = new FixedTimeProvider(BackupTime);
        var pushSubscriptions = new WebPushSubscriptionService(database, protector, timeProvider);
        return new ConfigurationBackupService(database, protector, new AppLinkService(), pushSubscriptions, new ScheduleService(timeProvider), timeProvider);
    }

    private static WebPushSubscriptionInput CreatePushSubscription()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return new WebPushSubscriptionInput(
            "https://push.example.test/send/recovery-device",
            WebPushEncoding.EncodeBase64Url(publicKey),
            WebPushEncoding.EncodeBase64Url(Enumerable.Range(1, 16).Select(value => (byte)value).ToArray()),
            null,
            "Recovery phone",
            "Test browser");
    }

    private static void ConfigureSettings(SettingsRecord settings, ISecretProtector protector, string apiKey)
    {
        settings.OnboardingCompleted = true;
        settings.OnboardingStep = 4;
        settings.TrueNasUsername = "autoupdate";
        settings.TrueNasApiKeyEncrypted = protector.Protect(apiKey);
        settings.VerifyTls = false;
        settings.SchedulerEnabled = true;
        settings.CronExpression = "0 23 * * 6";
        settings.TimeZoneId = "America/Los_Angeles";
        settings.NotifyManualApproval = true;
        settings.NotifyAutomaticFailure = true;
        settings.NotifyAutomaticBlocked = false;
        settings.NotifyRollback = true;
        settings.NotifyAutomaticSuccess = false;
        settings.NotifyScheduledCheckFailure = true;
        settings.NotifyConnectionFailure = false;
        settings.EmailEnabled = true;
        settings.EmailRecipientsJson = "[\"admin@example.test\",\"ops@example.test\"]";
        settings.WebhookEnabled = true;
        settings.WebhookUrl = "https://hooks.example.test/truenas";
        settings.WebhookAuthorizationEncrypted = protector.Protect("Bearer original");
        settings.WebhookHeadersEncrypted = protector.Protect("X-Test: original");
        settings.WebhookTimeoutSeconds = 37;
        settings.VerificationTimeoutSeconds = 420;
        settings.ConnectionFailureCooldownMinutes = 720;
        settings.HistoryRetentionDays = 45;
        settings.ManagerAppId = "truenas-app-manager";
        settings.PortalHostOverride = "http://truenas.local";
        settings.GitHubEnrichmentEnabled = false;
        settings.UptimeKumaBaseUrl = "http://kuma.local:3001/";
        settings.UptimeKumaBrowserUrl = "https://status.example.test/";
        settings.UptimeKumaApiKeyEncrypted = protector.Protect("kuma-original");
        settings.UptimeKumaVerifyTls = false;
        settings.UptimeKumaRefreshIntervalSeconds = 90;
    }

    private static async Task SeedAppsAndHistoryAsync(TestDatabase database)
    {
        await using var db = await database.CreateDbContextAsync();
        db.Apps.Add(CreateConfiguredApp("plex", AppPolicy.AutoUpdate));
        db.UptimeKumaMonitors.Add(new UptimeKumaMonitorRecord { MonitorId = "7", AppId = "plex", Name = "Plex", Type = "http", Status = UptimeKumaMonitorStatus.Up, IsPresent = true, LastSeenUtc = BackupTime.UtcDateTime });
        db.UpdateRuns.Add(new UpdateRun { Trigger = RunTrigger.CheckNow, StartedUtc = BackupTime.UtcDateTime, Status = RunStatus.Succeeded });
        await db.SaveChangesAsync();
    }

    private static AppRecord CreateConfiguredApp(string id, AppPolicy policy) => new()
    {
        Id = id,
        Name = id,
        IsInstalled = true,
        LastSeenUtc = BackupTime.UtcDateTime,
        Policy = policy,
        VersionScope = VersionScope.MinorAndPatch,
        SnapshotHostPaths = true,
        NotifySuccessOverride = false,
        DowntimeAction = DowntimeAction.NotifyOnly,
        NotifyOnDowntime = true,
        MaintenanceMode = true,
        IsFavorite = true,
        GroupName = "Media",
        LocalPortalUrl = $"http://truenas.local/{id}",
        RemotePortalUrl = $"https://{id}.example.test/"
    };
}
