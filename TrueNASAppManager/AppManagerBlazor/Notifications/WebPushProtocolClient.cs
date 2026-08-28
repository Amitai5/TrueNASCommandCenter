using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Notifications;

public interface IWebPushProtocolClient
{
    /// <summary>Sends one payload-free, authenticated Web Push wake-up.</summary>
    /// <param name="request">The target endpoint, VAPID identity, and retention settings.</param>
    /// <param name="cancellationToken">A token that cancels transport.</param>
    /// <returns>The successful push-service HTTP status code.</returns>
    Task<int> SendAsync(WebPushProtocolRequest request, CancellationToken cancellationToken = default);
}

public sealed class WebPushProtocolClient(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : IWebPushProtocolClient
{
    /// <inheritdoc />
    public async Task<int> SendAsync(WebPushProtocolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = ValidateEndpoint(request.Endpoint);
        var authorization = CreateVapidAuthorization(endpoint, request.VapidPublicKey, request.VapidPrivateKey);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            // Deliberately send no event payload through third-party browser push services.
            Content = new ByteArrayContent([])
        };
        message.Headers.TryAddWithoutValidation("TTL", Math.Clamp(request.TimeToLiveSeconds, 0, 2_419_200).ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("Urgency", "high");
        message.Headers.TryAddWithoutValidation("Authorization", authorization);

        var client = httpClientFactory.CreateClient("web-push");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new WebPushProtocolException(response.StatusCode, $"The browser push service returned HTTP {(int)response.StatusCode}.");
        }

        return (int)response.StatusCode;
    }

    private static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(endpointUri.Host) ||
            !string.IsNullOrEmpty(endpointUri.UserInfo) ||
            !string.IsNullOrEmpty(endpointUri.Fragment))
        {
            throw new InvalidOperationException("The Web Push endpoint must be an absolute HTTPS URL without credentials or a fragment.");
        }

        return endpointUri;
    }

    private string CreateVapidAuthorization(Uri endpoint, string publicKeyValue, string privateKeyValue)
    {
        var publicKey = WebPushEncoding.DecodeBase64Url(publicKeyValue, "VAPID public key");
        var privateKey = WebPushEncoding.DecodeBase64Url(privateKeyValue, "VAPID private key");
        if (publicKey.Length != 65 || publicKey[0] != 4 || privateKey.Length != 32)
        {
            throw new InvalidOperationException("The saved VAPID identity is invalid.");
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
        var header = WebPushEncoding.EncodeBase64Url("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        var claims = WebPushEncoding.EncodeBase64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = timeProvider.GetUtcNow().AddHours(12).ToUnixTimeSeconds(),
            sub = "https://truenas.local"
        }));
        var unsignedToken = $"{header}.{claims}";
        var unsignedBytes = Encoding.ASCII.GetBytes(unsignedToken);
        var signature = signer.SignData(unsignedBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!signer.VerifyData(unsignedBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException("The saved VAPID key pair does not match.");
        }

        return $"vapid t={unsignedToken}.{WebPushEncoding.EncodeBase64Url(signature)}, k={publicKeyValue}";
    }
}

public sealed class WebPushProtocolException(HttpStatusCode statusCode, string message) : Exception(message)
{
    /// <summary>Gets the status returned by the browser push service.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
