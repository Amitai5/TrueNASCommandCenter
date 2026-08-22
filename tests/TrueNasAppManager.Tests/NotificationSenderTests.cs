using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;
using TrueNasAppManager.Notifications;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class NotificationSenderTests
{
    [TestMethod]
    public void EmailFactory_BuildsTrueNasMailRequest()
    {
        var notification = Notification("<Update>", "App & update completed.");

        var message = EmailMessageFactory.Create(notification, ["admin@example.test"]);

        Assert.AreEqual("<Update>", message.Subject);
        StringAssert.Contains(message.Text, "App & update");
        StringAssert.Contains(message.Text, "Reason: TEST");
        CollectionAssert.AreEqual(new[] { "admin@example.test" }, message.Recipients.ToArray());
    }

    [TestMethod]
    public async Task EmailSender_UsesTrueNasAdministratorsWhenRecipientsAreBlank()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings => settings.EmailRecipientsJson = null);
        var client = new RecordingMailClient();
        var sender = new EmailNotificationSender(database.CreateSettingsService(), client, NullLogger<EmailNotificationSender>.Instance);

        var result = await sender.SendAsync(Notification("Test", "Message"));

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(client.Message);
        Assert.IsEmpty(client.Message.Recipients);
    }

    [TestMethod]
    public async Task Webhook_Treats2xxAsSuccessAndSendsSecretHeaders()
    {
        await using var database = await WebhookDatabaseAsync();
        var handler = new SequenceHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var sender = Sender(database, handler);

        var result = await sender.SendAsync(Notification("Test", "Message"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(204, result.HttpStatusCode);
        Assert.AreEqual("******", handler.CapturedHeaders.Single()["Authorization"]);
        Assert.AreEqual("secret-value", handler.CapturedHeaders.Single()["X-Secret"]);
    }

    [TestMethod]
    public async Task Webhook_RetriesServerFailureButNotNormal4xx()
    {
        await using var database = await WebhookDatabaseAsync();
        var retryHandler = new SequenceHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var retrySender = Sender(database, retryHandler);

        var retryResult = await retrySender.SendAsync(Notification("Retry", "Message"));

        Assert.IsTrue(retryResult.Success);
        Assert.AreEqual(2, retryHandler.Calls);

        var badRequestHandler = new SequenceHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var badRequestSender = Sender(database, badRequestHandler);

        var badRequestResult = await badRequestSender.SendAsync(Notification("No retry", "Message"));

        Assert.IsFalse(badRequestResult.Success);
        Assert.AreEqual(1, badRequestHandler.Calls);
    }

    [TestMethod]
    public async Task Webhook_TimeoutErrorDoesNotExposeSecret()
    {
        await using var database = await WebhookDatabaseAsync();
        var sender = new WebhookNotificationSender(
            new FakeHttpClientFactory(new ThrowingHandler()),
            database.CreateSettingsService(),
            TestDatabase.TrueNasEndpoint,
            new ImmediateTimeProvider(),
            NullLogger<WebhookNotificationSender>.Instance);

        var result = await sender.SendAsync(Notification("Timeout", "Message"));

        Assert.IsFalse(result.Success);
        Assert.DoesNotContain("webhook-secret", result.Error ?? string.Empty);
        Assert.AreEqual("Webhook delivery failed after retries.", result.Error);
    }

    private static async Task<TestDatabase> WebhookDatabaseAsync()
    {
        var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings =>
        {
            settings.WebhookUrl = "https://hooks.example.test/events";
            settings.WebhookAuthorizationEncrypted = protector.Protect("******");
            settings.WebhookHeadersEncrypted = protector.Protect("X-Secret: secret-value");
            settings.WebhookTimeoutSeconds = 10;
        });
        return database;
    }

    private static WebhookNotificationSender Sender(TestDatabase database, HttpMessageHandler handler) =>
        new(
            new FakeHttpClientFactory(handler),
            database.CreateSettingsService(),
            TestDatabase.TrueNasEndpoint,
            new ImmediateTimeProvider(),
            NullLogger<WebhookNotificationSender>.Instance);

    private static NotificationEvent Notification(string subject, string message) =>
        new(
            Guid.NewGuid(),
            NotificationEventType.AutomaticUpdateSucceeded,
            new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc),
            $"test|{Guid.NewGuid()}",
            subject,
            message,
            "TEST",
            "app",
            "App",
            "1.0.0",
            "1.1.0");

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("****** timed out.");
    }

    private sealed class RecordingMailClient : ITrueNasClient
    {
        public bool? HasWriteAccess => true;
        public bool? HasMailWriteAccess => true;
        public TrueNasMailMessage? Message { get; private set; }
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "Connected", true, true, true));
        public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrueNasAppDto>>([]);
        public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(string appId, string targetVersion = "latest", CancellationToken cancellationToken = default) => Task.FromResult(new TrueNasUpgradeSummaryDto());
        public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<long> StartAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StopAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartUpgradeAsync(string appId, string targetVersion, bool snapshotHostPaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> StartRollbackAsync(string appId, string targetVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendMailAsync(TrueNasMailMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<TrueNasLogEntry> FollowContainerLogsAsync(TrueNasContainerLogRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task ResetConnectionAsync() => Task.CompletedTask;
    }
}
