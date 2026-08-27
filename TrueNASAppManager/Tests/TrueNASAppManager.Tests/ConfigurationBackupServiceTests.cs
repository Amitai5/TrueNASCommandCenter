using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Scheduling;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class ConfigurationBackupServiceTests
{
    private static readonly DateTimeOffset BackupTime = new(2026, 8, 23, 1, 30, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SafeBackup_RoundTripRestoresConfigurationAndRetainsSecretsAndHistory()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        await SeedAppsAndHistoryAsync(database);
        var service = CreateService(database, protector);
        var backup = await service.ExportSafeAsync();
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
            (await mutate.UptimeKumaMonitors.SingleAsync()).AppId = null;
            mutate.Apps.Add(CreateConfiguredApp("unlisted", AppPolicy.NotifyOnly));
            await mutate.SaveChangesAsync();
        }

        var result = await service.ImportAsync(backup.Json, password: null);

        Assert.AreEqual(1, result.AppsRestored);
        Assert.IsFalse(result.SecretsRestored);
        await using var verify = await database.CreateDbContextAsync();
        var restoredSettings = await verify.Settings.SingleAsync();
        Assert.IsTrue(restoredSettings.SchedulerEnabled);
        Assert.AreEqual("replacement-key", protector.Unprotect(restoredSettings.TrueNasApiKeyEncrypted!));
        Assert.IsTrue(restoredSettings.UptimeKumaEnabled);
        Assert.AreEqual("http://kuma.local:3001/", restoredSettings.UptimeKumaBaseUrl);
        Assert.AreEqual("kuma-replacement", protector.Unprotect(restoredSettings.UptimeKumaApiKeyEncrypted!));
        var restoredPlex = await verify.Apps.SingleAsync(app => app.Id == "plex");
        Assert.AreEqual("Plex current", restoredPlex.Name);
        Assert.AreEqual(AppPolicy.AutoUpdate, restoredPlex.Policy);
        Assert.AreEqual("plex", (await verify.UptimeKumaMonitors.SingleAsync()).AppId);
        Assert.AreEqual(AppPolicy.NotifyOnly, (await verify.Apps.SingleAsync(app => app.Id == "unlisted")).Policy);
        Assert.AreEqual(1, await verify.UpdateRuns.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EncryptedBackup_CorrectPasswordRestoresSecretsAndWrongPasswordDoesNotMutate()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        await SeedAppsAndHistoryAsync(database);
        var service = CreateService(database, protector);
        var backup = await service.ExportEncryptedAsync("correct horse battery staple");
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
    public async Task EncryptedBackup_TamperedCiphertextIsRejected()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings => ConfigureSettings(settings, protector, "original-key"));
        var service = CreateService(database, protector);
        var backup = await service.ExportEncryptedAsync("correct horse battery staple");
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        document["encryption"]!["ciphertext"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(document.ToJsonString(), "correct horse battery staple"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SafeBackup_ImportCreatesPlaceholderAndPreservesUnlistedApp()
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
        var backup = await sourceService.ExportSafeAsync();

        await using var targetDatabase = new TestDatabase();
        var targetProtector = targetDatabase.CreateProtector();
        await targetDatabase.InitializeAsync(settings => ConfigureSettings(settings, targetProtector, "target-key"));
        await using (var target = await targetDatabase.CreateDbContextAsync())
        {
            target.Apps.Add(CreateConfiguredApp("unlisted", AppPolicy.NotifyOnly));
            await target.SaveChangesAsync();
        }
        var targetService = CreateService(targetDatabase, targetProtector);

        var result = await targetService.ImportAsync(backup.Json, password: null);

        Assert.AreEqual(1, result.AppsRestored);
        await using var verify = await targetDatabase.CreateDbContextAsync();
        var placeholder = await verify.Apps.SingleAsync(app => app.Id == "missing");
        Assert.IsFalse(placeholder.IsInstalled);
        Assert.AreEqual(AppPolicy.AutoUpdate, placeholder.Policy);
        Assert.AreEqual("https://missing.example.test/", placeholder.RemotePortalUrl);
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
        var backup = await service.ExportSafeAsync();
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        document["configuration"]!["settings"]!["schedulerEnabled"] = false;
        document["configuration"]!["apps"]![0]!["remotePortalUrl"] = "javascript:alert(1)";

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(document.ToJsonString(), password: null));

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
        var backup = await service.ExportSafeAsync();
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        var apps = document["configuration"]!["apps"]!.AsArray();
        var duplicate = apps[0]!.DeepClone();
        duplicate["appId"] = "duplicate-app";
        duplicate["name"] = "Duplicate app";
        apps.Add(duplicate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(document.ToJsonString(), password: null));

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
        var backup = await service.ExportSafeAsync();
        var document = JsonNode.Parse(backup.Json)!.AsObject();
        document["schemaVersion"] = schemaVersion;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(document.ToJsonString(), password: null));
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(oversized, password: null));
    }

    private static ConfigurationBackupService CreateService(TestDatabase database, ISecretProtector protector)
    {
        var timeProvider = new FixedTimeProvider(BackupTime);
        return new ConfigurationBackupService(database, protector, new AppLinkService(), new ScheduleService(timeProvider), timeProvider);
    }

    private static void ConfigureSettings(SettingsRecord settings, ISecretProtector protector, string apiKey)
    {
        settings.OnboardingCompleted = true;
        settings.TrueNasUsername = "autoupdate";
        settings.TrueNasApiKeyEncrypted = protector.Protect(apiKey);
        settings.VerifyTls = true;
        settings.SchedulerEnabled = true;
        settings.CronExpression = "0 23 * * 6";
        settings.TimeZoneId = "America/Los_Angeles";
        settings.EmailEnabled = true;
        settings.EmailRecipientsJson = "[\"admin@example.test\"]";
        settings.WebhookEnabled = true;
        settings.WebhookUrl = "https://hooks.example.test/truenas";
        settings.WebhookAuthorizationEncrypted = protector.Protect("Bearer original");
        settings.WebhookHeadersEncrypted = protector.Protect("X-Test: original");
        settings.PortalHostOverride = "https://truenas.local";
        settings.UptimeKumaBaseUrl = "http://kuma.local:3001/";
        settings.UptimeKumaBrowserUrl = "https://status.example.test/";
        settings.UptimeKumaApiKeyEncrypted = protector.Protect("kuma-original");
        settings.UptimeKumaVerifyTls = true;
        settings.UptimeKumaRefreshIntervalSeconds = 60;
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
        DowntimeAction = DowntimeAction.NotifyOnly,
        NotifyOnDowntime = true,
        LocalPortalUrl = $"http://truenas.local/{id}",
        RemotePortalUrl = $"https://{id}.example.test/"
    };
}
