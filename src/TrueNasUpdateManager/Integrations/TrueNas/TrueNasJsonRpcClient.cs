using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Services;

namespace TrueNasUpdateManager.Integrations.TrueNas;

public sealed class TrueNasJsonRpcClient(
    IWebSocketTransportFactory transportFactory,
    SettingsService settingsService,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<TrueNasJsonRpcClient> logger) : ITrueNasClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private IWebSocketTransport? transport;
    private CancellationTokenSource? receiveCancellation;
    private Task? receiveTask;
    private string? connectionFingerprint;
    private bool authenticated;
    private bool rolesDetected;
    private bool hasReadAccess;
    private bool hasWriteAccess;
    private long nextRequestId;

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ResetConnectionAsync();
            await EnsureConnectedAsync(cancellationToken);
            var pong = await CallAsync<string>("core.ping", [], cancellationToken);
            if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
            {
                throw new TrueNasClientException("PING_FAILED", "TrueNAS returned an unexpected ping response.");
            }

            _ = await CallAsync<IReadOnlyList<TrueNasAppDto>>(
                "app.query",
                [Array.Empty<object>(), new { extra = new { retrieve_config = false, include_app_schema = false } }],
                cancellationToken);

            await RecordConnectionResultAsync(true, null, null, cancellationToken);
            var writeMessage = rolesDetected && !hasWriteAccess
                ? "Connected, but APPS_WRITE was not detected."
                : "Connected and app discovery succeeded.";
            return new ConnectionTestResult(true, writeMessage, true, !rolesDetected || hasWriteAccess);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var (code, message) = SanitizeException(exception);
            await RecordConnectionResultAsync(false, code, message, cancellationToken);
            return new ConnectionTestResult(false, message, false, false, code);
        }
    }

    public Task<IReadOnlyList<TrueNasAppDto>> QueryAppsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<TrueNasAppDto>>(
            "app.query",
            [Array.Empty<object>(), new { extra = new { retrieve_config = false, include_app_schema = false } }],
            cancellationToken);

    public Task<TrueNasAppDto> GetAppAsync(string appId, CancellationToken cancellationToken = default) =>
        CallAsync<TrueNasAppDto>(
            "app.get_instance",
            [appId, new { extra = new { retrieve_config = false, include_app_schema = false } }],
            cancellationToken);

    public Task<IReadOnlyList<string>> GetOutdatedImagesAsync(
        string appId,
        CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<string>>("app.outdated_docker_images", [appId], cancellationToken);

    public Task<TrueNasUpgradeSummaryDto> GetUpgradeSummaryAsync(
        string appId,
        string targetVersion = "latest",
        CancellationToken cancellationToken = default) =>
        CallAsync<TrueNasUpgradeSummaryDto>(
            "app.upgrade_summary",
            [appId, new { app_version = targetVersion }],
            cancellationToken);

    public Task<IReadOnlyList<string>> GetRollbackVersionsAsync(
        string appId,
        CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<string>>("app.rollback_versions", [appId], cancellationToken);

    public Task<long> StartUpgradeAsync(
        string appId,
        string targetVersion,
        bool snapshotHostPaths,
        CancellationToken cancellationToken = default) =>
        CallAsync<long>(
            "app.upgrade",
            [
                appId,
                new
                {
                    app_version = targetVersion,
                    values = new Dictionary<string, object>(),
                    snapshot_hostpaths = snapshotHostPaths
                }
            ],
            cancellationToken);

    public Task<long> StartImageRefreshAsync(string appId, CancellationToken cancellationToken = default) =>
        CallAsync<long>("app.pull_images", [appId, new { redeploy = true }], cancellationToken);

    public Task<long> StartRollbackAsync(
        string appId,
        string targetVersion,
        CancellationToken cancellationToken = default) =>
        CallAsync<long>(
            "app.rollback",
            [appId, new { app_version = targetVersion, rollback_snapshot = true }],
            cancellationToken);

    public async Task WaitForJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        _ = await CallAsync<JsonElement>("core.job_wait", [jobId], cancellationToken);
    }

    public async Task ResetConnectionAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            await DisposeTransportAsync();
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task<T> CallAsync<T>(
        string method,
        object?[] parameters,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await SendRequestAsync<T>(method, parameters, cancellationToken);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        var options = await settingsService.GetConnectionOptionsAsync(cancellationToken);
        var fingerprint = string.Join(
            '|',
            options.ServerUri,
            options.Username,
            options.VerifyTls,
            options.AllowInsecureWebSocket,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey))));

        if (transport?.State == WebSocketState.Open &&
            authenticated &&
            string.Equals(connectionFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (transport?.State == WebSocketState.Open &&
                authenticated &&
                string.Equals(connectionFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            await DisposeTransportAsync();
            transport = transportFactory.Create();
            receiveCancellation = new CancellationTokenSource();

            try
            {
                await transport.ConnectAsync(options, cancellationToken);
                receiveTask = ReceiveLoopAsync(transport, receiveCancellation.Token);
                TrueNasAuthResponseDto auth;
                try
                {
                    auth = await SendRequestAsync<TrueNasAuthResponseDto>(
                        "auth.login_ex",
                        [
                            new
                            {
                                mechanism = "API_KEY_PLAIN",
                                username = options.Username,
                                api_key = options.ApiKey,
                                login_options = new { user_info = true, reconnect_token = false }
                            }
                        ],
                        cancellationToken);
                }
                catch (TrueNasClientException exception) when (exception.Code == "-32602")
                {
                    // TrueNAS 25.10 API_KEY_PLAIN predates the required username field.
                    auth = await SendRequestAsync<TrueNasAuthResponseDto>(
                        "auth.login_ex",
                        [
                            new
                            {
                                mechanism = "API_KEY_PLAIN",
                                api_key = options.ApiKey,
                                login_options = new { user_info = true, reconnect_token = false }
                            }
                        ],
                        cancellationToken);
                }

                if (!string.Equals(auth.ResponseType, "SUCCESS", StringComparison.Ordinal))
                {
                    throw new TrueNasClientException(
                        "AUTHENTICATION_FAILED",
                        $"TrueNAS authentication returned {auth.ResponseType}.");
                }

                ReadRoles(auth.UserInfo);
                authenticated = true;
                connectionFingerprint = fingerprint;
            }
            catch
            {
                await DisposeTransportAsync();
                throw;
            }
        }
        catch (WebSocketException exception)
        {
            throw ClassifyWebSocketException(exception);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task<T> SendRequestAsync<T>(
        string method,
        object?[] parameters,
        CancellationToken cancellationToken)
    {
        var activeTransport = transport;
        if (activeTransport?.State != WebSocketState.Open)
        {
            throw new TrueNasClientException("NETWORK_ERROR", "The TrueNAS WebSocket is not connected.");
        }

        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate JSON-RPC request identifier was generated.");
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            },
            JsonOptions);

        try
        {
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                await activeTransport.SendAsync(payload, cancellationToken);
            }
            finally
            {
                sendGate.Release();
            }

            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            var value = result.Deserialize<T>(JsonOptions);
            return value ?? throw new TrueNasClientException(
                "INVALID_RESPONSE",
                $"TrueNAS returned an empty result for {method}.");
        }
        catch (TimeoutException exception)
        {
            throw new TrueNasClientException("TIMEOUT", $"TrueNAS did not respond to {method} in time.", exception);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(IWebSocketTransport activeTransport, CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await activeTransport.ReceiveAsync(cancellationToken);
                if (message is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) ||
                    !TryReadRequestId(idElement, out var id) ||
                    !pending.TryGetValue(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(ParseRpcError(error));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(
                        new TrueNasClientException("INVALID_RESPONSE", "TrueNAS returned a malformed response."));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
            logger.LogWarning("TrueNAS WebSocket receive loop stopped: {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            authenticated = false;
            var error = terminalError is null
                ? new TrueNasClientException("CONNECTION_CLOSED", "The TrueNAS connection closed.")
                : new TrueNasClientException("NETWORK_ERROR", "The TrueNAS connection was interrupted.", terminalError);
            foreach (var completion in pending.Values)
            {
                completion.TrySetException(error);
            }
        }
    }

    private void ReadRoles(JsonElement? userInfo)
    {
        rolesDetected = false;
        hasReadAccess = false;
        hasWriteAccess = false;
        if (userInfo is null)
        {
            return;
        }

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectRoleStrings(userInfo.Value, roles);
        rolesDetected = roles.Any(role => role.EndsWith("_READ", StringComparison.OrdinalIgnoreCase) ||
                                          role.EndsWith("_WRITE", StringComparison.OrdinalIgnoreCase));
        hasReadAccess = roles.Contains("APPS_READ") || roles.Contains("APPS_WRITE");
        hasWriteAccess = roles.Contains("APPS_WRITE");
    }

    private static void CollectRoleStrings(JsonElement element, ISet<string> roles)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectRoleStrings(property.Value, roles);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectRoleStrings(item, roles);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    roles.Add(value);
                }

                break;
        }
    }

    private async Task RecordConnectionResultAsync(
        bool success,
        string? code,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
        if (success)
        {
            settings.LastConnectionSuccessUtc = timeProvider.GetUtcNow().UtcDateTime;
            settings.LastConnectionErrorCode = null;
            settings.LastConnectionError = null;
        }
        else
        {
            settings.LastConnectionErrorCode = code;
            settings.LastConnectionError = error;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DisposeTransportAsync()
    {
        authenticated = false;
        connectionFingerprint = null;
        receiveCancellation?.Cancel();

        var oldTransport = transport;
        transport = null;
        if (oldTransport is not null)
        {
            try
            {
                await oldTransport.CloseAsync(CancellationToken.None);
            }
            catch
            {
            }

            await oldTransport.DisposeAsync();
        }

        if (receiveTask is not null && receiveTask.Id != Task.CurrentId)
        {
            try
            {
                await receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        receiveTask = null;
        receiveCancellation?.Dispose();
        receiveCancellation = null;
    }

    private static bool TryReadRequestId(JsonElement idElement, out long id)
    {
        id = 0;
        if (idElement.ValueKind == JsonValueKind.Number)
        {
            return idElement.TryGetInt64(out id);
        }

        return idElement.ValueKind == JsonValueKind.String &&
               long.TryParse(idElement.GetString(), out id);
    }

    private static TrueNasClientException ParseRpcError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement)
            ? codeElement.GetInt32().ToString()
            : "RPC_ERROR";
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;
        if (error.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("reason", out var reason))
        {
            message = reason.GetString();
        }

        return new TrueNasClientException(code, Sanitize(message ?? "TrueNAS rejected the request."));
    }

    private static TrueNasClientException ClassifyWebSocketException(WebSocketException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        var code = message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            ? "TLS_FAILURE"
            : "NETWORK_ERROR";
        return new TrueNasClientException(code, code == "TLS_FAILURE"
            ? "TLS certificate validation failed."
            : "Unable to connect to TrueNAS.", exception);
    }

    private static (string Code, string Message) SanitizeException(Exception exception) =>
        exception switch
        {
            TrueNasClientException clientException => (clientException.Code, Sanitize(clientException.Message)),
            InvalidOperationException => ("CONFIGURATION_ERROR", Sanitize(exception.Message)),
            _ => ("CONNECTION_ERROR", "The TrueNAS connection test failed.")
        };

    private static string Sanitize(string value) =>
        value.Length <= 512 ? value : value[..512];

    public async ValueTask DisposeAsync()
    {
        await ResetConnectionAsync();
        connectionGate.Dispose();
        sendGate.Dispose();
    }
}
