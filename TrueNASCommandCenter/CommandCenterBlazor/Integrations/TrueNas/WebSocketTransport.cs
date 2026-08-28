using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Integrations.TrueNas;

public interface IWebSocketTransport : IAsyncDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken);
    Task SendAsync(string message, CancellationToken cancellationToken);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

public interface IWebSocketTransportFactory
{
    IWebSocketTransport Create();
}

public sealed class ClientWebSocketTransportFactory(ILoggerFactory loggerFactory) : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => new ClientWebSocketTransport(loggerFactory.CreateLogger<ClientWebSocketTransport>());
}

public sealed class ClientWebSocketTransport(ILogger<ClientWebSocketTransport> logger) : IWebSocketTransport
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private readonly ClientWebSocket socket = new();

    public WebSocketState State => socket.State;

    public async Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken)
    {
        var endpoint = GetSafeEndpoint(options.ServerUri);
        logger.LogInformation(
            "Starting TrueNAS WebSocket transport connection to {Endpoint}. Host={Host} Port={Port} VerifyTls={VerifyTls} TimeoutSeconds={TimeoutSeconds}",
            endpoint,
            options.ServerUri.DnsSafeHost,
            options.ServerUri.Port,
            options.VerifyTls,
            ConnectionTimeout.TotalSeconds);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(options.ServerUri.DnsSafeHost, cancellationToken);
            logger.LogInformation(
                "Resolved TrueNAS host {Host} to {Addresses}",
                options.ServerUri.DnsSafeHost,
                addresses.Length == 0 ? "no addresses" : string.Join(", ", addresses.Select(static address => address.ToString())));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "DNS preflight failed for TrueNAS host {Host}; the WebSocket connection will still be attempted",
                options.ServerUri.DnsSafeHost);
        }

        if (!options.VerifyTls)
        {
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        try
        {
            await socket.ConnectAsync(options.ServerUri, timeout.Token);
            logger.LogInformation(
                "TrueNAS WebSocket transport connected to {Endpoint} in {ElapsedMilliseconds} ms. State={State}",
                endpoint,
                stopwatch.ElapsedMilliseconds,
                socket.State);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutException = new WebSocketException(
                WebSocketError.Faulted,
                $"The TrueNAS WebSocket connection timed out after {ConnectionTimeout.TotalSeconds:0} seconds.",
                exception);
            logger.LogError(
                timeoutException,
                "TrueNAS WebSocket transport connection to {Endpoint} timed out after {ElapsedMilliseconds} ms",
                endpoint,
                stopwatch.ElapsedMilliseconds);
            throw timeoutException;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "TrueNAS WebSocket transport connection to {Endpoint} failed after {ElapsedMilliseconds} ms. SocketState={State}",
                endpoint,
                stopwatch.ElapsedMilliseconds,
                socket.State);
            throw;
        }
    }

    public Task SendAsync(string message, CancellationToken cancellationToken) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            WebSocketMessageFlags.EndOfMessage,
            cancellationToken)
            .AsTask();

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogWarning(
                    "TrueNAS WebSocket received a close frame. Status={CloseStatus} Description={CloseDescription}",
                    result.CloseStatus?.ToString() ?? "none",
                    SanitizeCloseDescription(result.CloseStatusDescription));
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string GetSafeEndpoint(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static string SanitizeCloseDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        return value.Length <= 256 ? value : value[..256];
    }
}
