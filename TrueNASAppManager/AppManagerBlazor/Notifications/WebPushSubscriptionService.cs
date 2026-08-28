using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Notifications;

public interface IWebPushSubscriptionService
{
    /// <summary>Returns the persistent VAPID public key used when browsers subscribe.</summary>
    /// <param name="cancellationToken">A token that cancels key initialization.</param>
    /// <returns>The URL-safe Base64 encoded public key.</returns>
    Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers or refreshes a browser subscription after validating its endpoint and key material.</summary>
    /// <param name="subscription">The browser-provided subscription.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    Task RegisterAsync(WebPushSubscriptionInput subscription, CancellationToken cancellationToken = default);

    /// <summary>Removes the subscription associated with the specified browser endpoint.</summary>
    /// <param name="endpoint">The exact browser push endpoint.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    /// <returns>True when a saved subscription was removed.</returns>
    Task<bool> RemoveAsync(string endpoint, CancellationToken cancellationToken = default);

    /// <summary>Removes a saved browser device by its server-side identifier.</summary>
    /// <param name="subscriptionId">The saved subscription identifier.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    /// <returns>True when a saved subscription was removed.</returns>
    Task<bool> RemoveByIdAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Lists registered devices without returning endpoint or encryption-key secrets.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The registered device summaries.</returns>
    Task<IReadOnlyList<WebPushSubscriptionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds the saved identifier for an exact browser endpoint.</summary>
    /// <param name="endpoint">The browser push endpoint.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The subscription identifier, or null when the endpoint is not registered.</returns>
    Task<Guid?> FindIdByEndpointAsync(string endpoint, CancellationToken cancellationToken = default);

    /// <summary>Reports whether at least one non-expired push subscription is available.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>True when push delivery has at least one target.</returns>
    Task<bool> HasSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the protected VAPID identity and active subscriptions for one delivery operation.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The delivery configuration.</returns>
    Task<WebPushDeliveryConfiguration> GetDeliveryConfigurationAsync(CancellationToken cancellationToken = default);
}

public sealed class WebPushSubscriptionService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISecretProtector secretProtector,
    TimeProvider timeProvider) : IWebPushSubscriptionService
{
    private readonly SemaphoreSlim keyGate = new(1, 1);

    /// <inheritdoc />
    public async Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var keys = await EnsureVapidKeysAsync(cancellationToken);
        return keys.PublicKey;
    }

    /// <inheritdoc />
    public async Task RegisterAsync(WebPushSubscriptionInput subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ValidateSubscription(subscription);
        await EnsureVapidKeysAsync(cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (subscription.ExpirationTime is not null && subscription.ExpirationTime <= timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("The browser push subscription has already expired.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WebPushSubscriptions.SingleOrDefaultAsync(item => item.Endpoint == subscription.Endpoint, cancellationToken);
        if (existing is null)
        {
            existing = new WebPushSubscriptionRecord
            {
                Endpoint = subscription.Endpoint,
                CreatedUtc = now
            };
            db.WebPushSubscriptions.Add(existing);
        }

        existing.P256dh = subscription.P256dh;
        existing.Auth = subscription.Auth;
        existing.ExpirationUtc = subscription.ExpirationTime?.UtcDateTime;
        existing.DeviceName = NormalizeOptional(subscription.DeviceName, 128) ?? DescribeDevice(subscription.UserAgent);
        existing.UserAgent = NormalizeOptional(subscription.UserAgent, 512);
        existing.LastSeenUtc = now;
        existing.ConsecutiveFailures = 0;
        existing.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var subscription = await db.WebPushSubscriptions.SingleOrDefaultAsync(item => item.Endpoint == endpoint, cancellationToken);
        if (subscription is null)
        {
            return false;
        }

        db.WebPushSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveByIdAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var subscription = await db.WebPushSubscriptions.SingleOrDefaultAsync(item => item.Id == subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return false;
        }

        db.WebPushSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebPushSubscriptionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WebPushSubscriptions
            .AsNoTracking()
            .OrderByDescending(item => item.LastSeenUtc)
            .Select(item => new WebPushSubscriptionSummary(
                item.Id,
                item.DeviceName ?? "Browser device",
                item.CreatedUtc,
                item.LastSeenUtc,
                item.LastSuccessUtc,
                item.LastFailureUtc,
                item.ConsecutiveFailures))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid?> FindIdByEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WebPushSubscriptions
            .Where(item => item.Endpoint == endpoint)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WebPushSubscriptions.AnyAsync(item => item.ExpirationUtc == null || item.ExpirationUtc > now, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WebPushDeliveryConfiguration> GetDeliveryConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var keys = await EnsureVapidKeysAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var subscriptions = await db.WebPushSubscriptions
            .AsNoTracking()
            .Where(item => item.ExpirationUtc == null || item.ExpirationUtc > now)
            .OrderBy(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);
        return new WebPushDeliveryConfiguration(keys.PublicKey, keys.PrivateKey, subscriptions);
    }

    private async Task<VapidKeys> EnsureVapidKeysAsync(CancellationToken cancellationToken)
    {
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.WebPushPublicKey) && !string.IsNullOrWhiteSpace(settings.WebPushPrivateKeyEncrypted))
            {
                return new VapidKeys(settings.WebPushPublicKey, secretProtector.Unprotect(settings.WebPushPrivateKeyEncrypted));
            }

            var generated = GenerateVapidKeys();
            settings.WebPushPublicKey = generated.PublicKey;
            settings.WebPushPrivateKeyEncrypted = secretProtector.Protect(generated.PrivateKey);
            await db.SaveChangesAsync(cancellationToken);
            return generated;
        }
        finally
        {
            keyGate.Release();
        }
    }

    private static VapidKeys GenerateVapidKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        if (parameters.Q.X is null || parameters.Q.Y is null || parameters.D is null)
        {
            throw new CryptographicException("The platform could not generate a complete P-256 VAPID key pair.");
        }

        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X.CopyTo(publicKey, 1);
        parameters.Q.Y.CopyTo(publicKey, 33);
        return new VapidKeys(WebPushEncoding.EncodeBase64Url(publicKey), WebPushEncoding.EncodeBase64Url(parameters.D));
    }

    private static void ValidateSubscription(WebPushSubscriptionInput subscription)
    {
        WebPushEncoding.ValidateSubscriptionMaterial(subscription.Endpoint, subscription.P256dh, subscription.Auth);
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string DescribeDevice(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Browser device";
        }

        var platform = userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            ? "iPhone or iPad"
            : userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
                ? "Android device"
                : userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                    ? "Windows device"
                    : userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
                        ? "Mac device"
                        : "Browser device";
        return platform;
    }

    private sealed record VapidKeys(string PublicKey, string PrivateKey);
}

public static class WebPushEncoding
{
    /// <summary>Encodes bytes using the URL-safe Base64 alphabet without padding.</summary>
    /// <param name="value">The bytes to encode.</param>
    /// <returns>The URL-safe Base64 representation.</returns>
    public static string EncodeBase64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a URL-safe Base64 value and provides a domain-specific validation error.</summary>
    /// <param name="value">The URL-safe Base64 input.</param>
    /// <param name="label">The input label used in validation errors.</param>
    /// <returns>The decoded bytes.</returns>
    public static byte[] DecodeBase64Url(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"The {label} is missing.");
        }

        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new InvalidOperationException($"The {label} is not valid Base64URL data.")
        };

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The {label} is not valid Base64URL data.", exception);
        }
    }

    /// <summary>Validates a browser push endpoint and its P-256 and authentication material.</summary>
    /// <param name="endpointValue">The browser push-service endpoint.</param>
    /// <param name="p256dh">The browser P-256 public key.</param>
    /// <param name="auth">The browser authentication secret.</param>
    public static void ValidateSubscriptionMaterial(string endpointValue, string p256dh, string auth)
    {
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpointValue.Length > 4096)
        {
            throw new InvalidOperationException("The browser returned an invalid HTTPS push endpoint.");
        }

        var publicKey = DecodeBase64Url(p256dh, "browser public key");
        var authSecret = DecodeBase64Url(auth, "browser authentication secret");
        if (publicKey.Length != 65 || publicKey[0] != 4)
        {
            throw new InvalidOperationException("The browser returned an invalid P-256 subscription key.");
        }

        if (authSecret.Length != 16)
        {
            throw new InvalidOperationException("The browser returned an invalid push authentication secret.");
        }
    }

    /// <summary>Validates that persisted VAPID public and private keys form one P-256 signing identity.</summary>
    /// <param name="publicKeyValue">The URL-safe Base64 public key.</param>
    /// <param name="privateKeyValue">The URL-safe Base64 private key.</param>
    public static void ValidateVapidKeyPair(string publicKeyValue, string privateKeyValue)
    {
        var publicKey = DecodeBase64Url(publicKeyValue, "VAPID public key");
        var privateKey = DecodeBase64Url(privateKeyValue, "VAPID private key");
        if (publicKey.Length != 65 || publicKey[0] != 4 || privateKey.Length != 32)
        {
            throw new InvalidOperationException("The VAPID key pair is invalid.");
        }

        using var signer = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey.AsSpan(1, 32).ToArray(),
                Y = publicKey.AsSpan(33, 32).ToArray()
            },
            D = privateKey
        });
        var challenge = "TrueNasAppManager:VAPID"u8.ToArray();
        var signature = signer.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!signer.VerifyData(challenge, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new InvalidOperationException("The VAPID public and private keys do not match.");
        }
    }
}
