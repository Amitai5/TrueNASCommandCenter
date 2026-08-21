using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Notifications;

namespace TrueNasUpdateManager.Tests;

[TestClass]
public sealed class NotificationSenderTests
{
    [TestMethod]
    public void EmailFactory_BuildsPlainAndEncodedHtmlBodies()
    {
        var notification = Notification("<Update>", "App & update completed.");

        var message = EmailMessageFactory.Create(
            notification,
            "Manager",
            "manager@example.test",
            ["admin@example.test"]);
        var body = Assert.IsInstanceOfType<MimeKit.MultipartAlternative>(message.Body);
        var parts = body.OfType<MimeKit.TextPart>().ToList();

        Assert.AreEqual("<Update>", message.Subject);
        Assert.IsTrue(parts.Any(part => part.IsPlain && part.Text.Contains("App & update")));
        Assert.IsTrue(parts.Any(part => part.IsHtml && part.Text.Contains("&lt;Update&gt;")));
        Assert.IsTrue(parts.Any(part => part.IsHtml && part.Text.Contains("App &amp; update")));
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
}
