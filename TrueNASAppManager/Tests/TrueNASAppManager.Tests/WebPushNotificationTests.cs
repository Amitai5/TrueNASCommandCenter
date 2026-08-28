using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Notifications;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class WebPushNotificationTests
{
    private static readonly DateTimeOffset TestTime = new(2026, 8, 27, 22, 15, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubscriptionService_FirstUse_GeneratesPersistentProtectedVapidIdentity()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var protector = database.CreateProtector();
        var service = new WebPushSubscriptionService(database, protector, new FixedTimeProvider(TestTime));

        var firstPublicKey = await service.GetPublicKeyAsync();
        var secondPublicKey = await service.GetPublicKeyAsync();
        var delivery = await service.GetDeliveryConfigurationAsync();

        Assert.AreEqual(firstPublicKey, secondPublicKey);
        Assert.HasCount(65, WebPushEncoding.DecodeBase64Url(firstPublicKey, "public key"));
        WebPushEncoding.ValidateVapidKeyPair(delivery.PublicKey, delivery.PrivateKey);
        await using var db = await database.CreateDbContextAsync();
        var settings = await db.Settings.SingleAsync();
        Assert.IsNotNull(settings.WebPushPrivateKeyEncrypted);
        Assert.AreNotEqual(delivery.PrivateKey, settings.WebPushPrivateKeyEncrypted);
        Assert.DoesNotContain(delivery.PrivateKey, settings.WebPushPrivateKeyEncrypted);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubscriptionService_RegisterSameEndpoint_RefreshesSingleDevice()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));
        var subscription = CreateSubscription("Kitchen iPad");

        await service.RegisterAsync(subscription);
        await service.RegisterAsync(subscription with { DeviceName = "Wall iPad" });

        var devices = await service.ListAsync();
        Assert.HasCount(1, devices);
        Assert.AreEqual("Wall iPad", devices[0].DeviceName);
        Assert.IsTrue(await service.HasSubscriptionsAsync());
        await using var db = await database.CreateDbContextAsync();
        Assert.AreEqual(1, await db.WebPushSubscriptions.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubscriptionService_FindAndRemove_UpdatesDeviceRegistry()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));
        var subscription = CreateSubscription("Phone");
        await service.RegisterAsync(subscription);
        var subscriptionId = await service.FindIdByEndpointAsync(subscription.Endpoint);

        Assert.IsNotNull(subscriptionId);
        Assert.IsNull(await service.FindIdByEndpointAsync(" "));
        Assert.IsNull(await service.FindIdByEndpointAsync("https://push.example.test/send/missing"));
        Assert.IsFalse(await service.RemoveByIdAsync(Guid.NewGuid()));
        Assert.IsTrue(await service.RemoveAsync(subscription.Endpoint));
        Assert.IsFalse(await service.RemoveAsync(subscription.Endpoint));
        Assert.IsFalse(await service.RemoveAsync(string.Empty));
        await service.RegisterAsync(subscription);
        var replacementId = await service.FindIdByEndpointAsync(subscription.Endpoint);
        Assert.IsNotNull(replacementId);
        Assert.IsTrue(await service.RemoveByIdAsync(replacementId.Value));
        Assert.IsFalse(await service.HasSubscriptionsAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubscriptionService_ExpiredSubscription_IsRejected()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(CreateSubscription("Old device") with { ExpirationTime = TestTime.AddMinutes(-1) }));

        StringAssert.Contains(exception.Message, "already expired");
        Assert.IsFalse(await service.HasSubscriptionsAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubscriptionService_MissingDeviceName_UsesPlatformDescription()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));

        await service.RegisterAsync(CreateSubscription(string.Empty) with { UserAgent = "Mozilla/5.0 (Linux; Android 16)" });

        var device = (await service.ListAsync()).Single();
        Assert.AreEqual("Android device", device.DeviceName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("http://push.example.test/subscription")]
    [DataRow("https://user:password@push.example.test/subscription")]
    [DataRow("https://push.example.test/subscription#fragment")]
    public async Task SubscriptionService_UnsafeEndpoint_IsRejected(string endpoint)
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var service = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(CreateSubscription("Device") with { Endpoint = endpoint }));

        StringAssert.Contains(exception.Message, "invalid HTTPS push endpoint");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Encoding_InvalidSubscriptionKeyMaterial_IsRejected()
    {
        var structurallyValidPublicKey = new byte[65];
        structurallyValidPublicKey[0] = 4;
        var invalidPublicKey = Assert.Throws<InvalidOperationException>(() => WebPushEncoding.ValidateSubscriptionMaterial(
            "https://push.example.test/subscription",
            WebPushEncoding.EncodeBase64Url([1, 2, 3]),
            WebPushEncoding.EncodeBase64Url(new byte[16])));
        var invalidAuth = Assert.Throws<InvalidOperationException>(() => WebPushEncoding.ValidateSubscriptionMaterial(
            "https://push.example.test/subscription",
            WebPushEncoding.EncodeBase64Url(structurallyValidPublicKey),
            WebPushEncoding.EncodeBase64Url([1, 2, 3])));
        var invalidBase64 = Assert.Throws<InvalidOperationException>(() => WebPushEncoding.DecodeBase64Url("a", "test value"));

        StringAssert.Contains(invalidPublicKey.Message, "invalid P-256");
        StringAssert.Contains(invalidAuth.Message, "invalid push authentication secret");
        StringAssert.Contains(invalidBase64.Message, "not valid Base64URL");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProtocolClient_Send_UsesVapidAndSendsNoInternalPayload()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var subscriptions = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));
        var configuration = await subscriptions.GetDeliveryConfigurationAsync();
        var handler = new CapturingPushHandler(HttpStatusCode.Created);
        var client = new WebPushProtocolClient(new FakeHttpClientFactory(handler), new FixedTimeProvider(TestTime));

        var status = await client.SendAsync(new WebPushProtocolRequest("https://push.example.test/send/device-token", configuration.PublicKey, configuration.PrivateKey));

        Assert.AreEqual(201, status);
        Assert.AreEqual(1, handler.Calls);
        Assert.HasCount(0, handler.Body);
        Assert.AreEqual("3600", handler.Headers["TTL"]);
        Assert.AreEqual("high", handler.Headers["Urgency"]);
        StringAssert.StartsWith(handler.Headers["Authorization"], "vapid t=");
        AssertVapidToken(handler.Headers["Authorization"], configuration.PublicKey);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NotificationSender_GoneSubscription_RemovesExpiredDevice()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var subscriptions = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));
        await subscriptions.RegisterAsync(CreateSubscription("Old phone"));
        var protocol = new StubPushProtocolClient(new WebPushProtocolException(HttpStatusCode.Gone, "Gone"));
        var sender = new WebPushNotificationSender(subscriptions, protocol, database, new FixedTimeProvider(TestTime), NullLogger<WebPushNotificationSender>.Instance);

        var result = await sender.SendAsync(CreateNotification());

        Assert.IsFalse(result.Success);
        Assert.IsFalse(await subscriptions.HasSubscriptionsAsync());
        await using var db = await database.CreateDbContextAsync();
        Assert.AreEqual(0, await db.WebPushSubscriptions.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NotificationSender_Success_RecordsDeliveryWithoutSendingEventDetails()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var subscriptions = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));
        await subscriptions.RegisterAsync(CreateSubscription("Laptop"));
        var protocol = new StubPushProtocolClient(statusCode: 201);
        var sender = new WebPushNotificationSender(subscriptions, protocol, database, new FixedTimeProvider(TestTime), NullLogger<WebPushNotificationSender>.Instance);

        var result = await sender.SendAsync(CreateNotification());

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(protocol.Request);
        Assert.IsFalse(protocol.Request.ToString()!.Contains("private-app", StringComparison.Ordinal));
        await using var db = await database.CreateDbContextAsync();
        var subscription = await db.WebPushSubscriptions.SingleAsync();
        Assert.AreEqual(TestTime.UtcDateTime, subscription.LastSuccessUtc);
        Assert.AreEqual(0, subscription.ConsecutiveFailures);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NotificationSender_TransientFailure_RetainsDeviceAndRecordsFailure()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var subscriptions = new WebPushSubscriptionService(database, database.CreateProtector(), new FixedTimeProvider(TestTime));
        await subscriptions.RegisterAsync(CreateSubscription("Phone"));
        var protocol = new StubPushProtocolClient(new WebPushProtocolException(HttpStatusCode.ServiceUnavailable, "Unavailable"));
        var sender = new WebPushNotificationSender(subscriptions, protocol, database, new FixedTimeProvider(TestTime), NullLogger<WebPushNotificationSender>.Instance);

        var result = await sender.SendAsync(CreateNotification());

        Assert.IsFalse(result.Success);
        Assert.IsTrue(await subscriptions.HasSubscriptionsAsync());
        await using var db = await database.CreateDbContextAsync();
        var subscription = await db.WebPushSubscriptions.SingleAsync();
        Assert.AreEqual(TestTime.UtcDateTime, subscription.LastFailureUtc);
        Assert.AreEqual(1, subscription.ConsecutiveFailures);
        Assert.AreEqual("Unavailable", subscription.LastError);
    }

    private static WebPushSubscriptionInput CreateSubscription(string deviceName)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        var auth = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        return new WebPushSubscriptionInput(
            "https://push.example.test/send/device-token",
            WebPushEncoding.EncodeBase64Url(publicKey),
            WebPushEncoding.EncodeBase64Url(auth),
            null,
            deviceName,
            "Mozilla/5.0 Test Browser");
    }

    private static NotificationEvent CreateNotification() => new(
        Guid.NewGuid(),
        NotificationEventType.AppDowntime,
        TestTime.UtcDateTime,
        "downtime|private-app|incident",
        "Private app is down",
        "Internal host details",
        "APP_DOWN",
        "private-app",
        "Private App");

    private static void AssertVapidToken(string authorization, string publicKeyValue)
    {
        var tokenStart = authorization.IndexOf("t=", StringComparison.Ordinal) + 2;
        var tokenEnd = authorization.IndexOf(", k=", StringComparison.Ordinal);
        var token = authorization[tokenStart..tokenEnd];
        var segments = token.Split('.');
        Assert.HasCount(3, segments);
        using var claims = JsonDocument.Parse(WebPushEncoding.DecodeBase64Url(segments[1], "claims"));
        Assert.AreEqual("https://push.example.test", claims.RootElement.GetProperty("aud").GetString());
        Assert.AreEqual(TestTime.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

        var publicKey = WebPushEncoding.DecodeBase64Url(publicKeyValue, "public key");
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey.AsSpan(1, 32).ToArray(),
                Y = publicKey.AsSpan(33, 32).ToArray()
            }
        });
        var signature = WebPushEncoding.DecodeBase64Url(segments[2], "signature");
        Assert.IsTrue(verifier.VerifyData(Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private sealed class CapturingPushHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            Body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class StubPushProtocolClient(Exception? exception = null, int statusCode = 201) : IWebPushProtocolClient
    {
        public WebPushProtocolRequest? Request { get; private set; }

        public Task<int> SendAsync(WebPushProtocolRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return exception is null ? Task.FromResult(statusCode) : Task.FromException<int>(exception);
        }
    }
}
