using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Integrations.TrueNas;
using TrueNasUpdateManager.Notifications;
using TrueNasUpdateManager.Services;

namespace TrueNasUpdateManager.Tests;

internal sealed class TestDatabase : IDbContextFactory<AppDbContext>, IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"truenas-tests-{Guid.NewGuid():N}");
    private readonly DbContextOptions<AppDbContext> options;

    public TestDatabase()
    {
        Directory.CreateDirectory(directory);
        options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "test.db")}")
            .Options;
    }

    public DataPathOptions DataPath => new(directory);

    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public async Task InitializeAsync(Action<SettingsRecord>? configure = null)
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var settings = new SettingsRecord();
        configure?.Invoke(settings);
        db.Settings.Add(settings);
        await db.SaveChangesAsync();
    }

    public AesGcmSecretProtector CreateProtector()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APP_ENCRYPTION_KEY"] = key
            })
            .Build();
        return new AesGcmSecretProtector(DataPath, configuration);
    }

    public SettingsService CreateSettingsService() => new(this, CreateProtector());

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class ImmediateTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) =>
        new ImmediateTimer(callback, state, dueTime);

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback callback;
        private readonly object? state;
        private bool disposed;

        public ImmediateTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this.callback = callback;
            this.state = state;
            Schedule(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime);
            return !disposed;
        }

        public void Dispose()
        {
            disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Schedule(TimeSpan dueTime)
        {
            if (!disposed && dueTime != Timeout.InfiniteTimeSpan)
            {
                _ = Task.Run(() =>
                {
                    if (!disposed)
                    {
                        callback(state);
                    }
                });
            }
        }
    }
}

internal sealed class FakeEmailSender : IEmailNotificationSender
{
    public int Calls { get; private set; }

    public Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new NotificationDeliveryResult(true));
    }
}

internal sealed class FakeWebhookSender : IWebhookNotificationSender
{
    public int Calls { get; private set; }

    public Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new NotificationDeliveryResult(true, 204));
    }
}

internal sealed class NoopNotificationDispatcher : INotificationDispatcher
{
    public List<NotificationEvent> Events { get; } = [];

    public Task DispatchAsync(NotificationEvent notification, CancellationToken cancellationToken = default)
    {
        Events.Add(notification);
        return Task.CompletedTask;
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class SequenceHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new(responses);

    public int Calls { get; private set; }
    public List<Dictionary<string, string>> CapturedHeaders { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        CapturedHeaders.Add(request.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(",", header.Value),
            StringComparer.OrdinalIgnoreCase));
        if (responses.Count == 0)
        {
            throw new HttpRequestException("No fake response configured.");
        }

        return Task.FromResult(responses.Dequeue()(request));
    }
}

internal sealed class FakeWebSocketTransport : IWebSocketTransport
{
    private readonly Channel<string> incoming = Channel.CreateUnbounded<string>();

    public WebSocketState State { get; private set; } = WebSocketState.None;
    public Exception? ConnectException { get; set; }
    public Func<JsonElement, Task>? OnSend { get; set; }

    public Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken)
    {
        if (ConnectException is not null)
        {
            return Task.FromException(ConnectException);
        }

        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);
        if (OnSend is not null)
        {
            await OnSend(document.RootElement.Clone());
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) =>
        await incoming.Reader.ReadAsync(cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Respond(long id, object result)
    {
        incoming.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result
        }));
    }

    public void Error(long id, int code, string reason)
    {
        incoming.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message = "Call failed",
                data = new { reason }
            }
        }));
    }
}

internal sealed class FakeWebSocketTransportFactory(FakeWebSocketTransport transport)
    : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => transport;
}

internal sealed class SequenceWebSocketTransportFactory(params FakeWebSocketTransport[] transports)
    : IWebSocketTransportFactory
{
    private readonly Queue<FakeWebSocketTransport> transports = new(transports);

    public IWebSocketTransport Create() => transports.Dequeue();
}

internal static class TestClientFactory
{
    public static async Task<(TrueNasJsonRpcClient Client, FakeWebSocketTransport Transport, TestDatabase Database)> CreateAsync(Func<FakeWebSocketTransport, JsonElement, Task>? responder = null, ILogger<TrueNasJsonRpcClient>? logger = null)
    {
        var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings =>
        {
            settings.TrueNasUrl = "wss://truenas.test/api/current";
            settings.TrueNasUsername = "service";
            settings.TrueNasApiKeyEncrypted = protector.Protect("test-api-key");
        });

        var transport = new FakeWebSocketTransport();
        transport.OnSend = async request =>
        {
            var method = request.GetProperty("method").GetString();
            var id = request.GetProperty("id").GetInt64();
            if (method == "auth.login_ex")
            {
                transport.Respond(id, new
                {
                    response_type = "SUCCESS",
                    user_info = new
                    {
                        privilege = new { roles = new[] { "APPS_READ", "APPS_WRITE" } }
                    }
                });
                return;
            }

            if (responder is not null)
            {
                await responder(transport, request);
            }
        };

        var client = new TrueNasJsonRpcClient(
            new FakeWebSocketTransportFactory(transport),
            new SettingsService(database, protector),
            database,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero)),
            logger ?? NullLogger<TrueNasJsonRpcClient>.Instance);
        return (client, transport, database);
    }
}

internal sealed record RecordedLog(LogLevel Level, string Message, Exception? Exception);

/// <inheritdoc cref="ILogger{TCategoryName}"/>
internal sealed class RecordingLogger<TCategoryName> : ILogger<TCategoryName>
{
    public List<RecordedLog> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new RecordedLog(logLevel, formatter(state, exception), exception));
    }
}
