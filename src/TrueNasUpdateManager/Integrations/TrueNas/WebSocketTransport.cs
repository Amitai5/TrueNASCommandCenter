using System.Net.WebSockets;
using System.Text;
using TrueNasUpdateManager.Domain;

namespace TrueNasUpdateManager.Integrations.TrueNas;

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

public sealed class ClientWebSocketTransportFactory : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => new ClientWebSocketTransport();
}

public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly ClientWebSocket socket = new();

    public WebSocketState State => socket.State;

    public async Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken)
    {
        if (!options.VerifyTls)
        {
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        await socket.ConnectAsync(options.ServerUri, cancellationToken);
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
}
