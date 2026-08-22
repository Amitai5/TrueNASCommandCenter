using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
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

    private readonly ConcurrentDictionary<long, PendingRequest> pending = new();
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

    public bool? HasWriteAccess => rolesDetected ? hasWriteAccess : null;

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        var stage = "reset";
        logger.LogInformation("TrueNAS connection test {DiagnosticId} started", diagnosticId);
        try
        {
            await ResetConnectionAsync();
            stage = "connect-and-authenticate";
            await EnsureConnectedAsync(cancellationToken, diagnosticId);
            stage = "core.ping";
            var pong = await SendRequestAsync<string>("core.ping", [], cancellationToken, diagnosticId: diagnosticId);
            if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
            {
                throw new TrueNasClientException("PING_FAILED", "TrueNAS returned an unexpected ping response.");
            }

            stage = "app.query";
            _ = await SendRequestAsync<IReadOnlyList<TrueNasAppDto>>(
                "app.query",
                [Array.Empty<object>(), new { extra = new { retrieve_config = false, include_app_schema = false } }],
                cancellationToken,
                diagnosticId: diagnosticId);

            await RecordConnectionResultAsync(true, null, null, cancellationToken);
            var writeMessage = rolesDetected && !hasWriteAccess
                ? "Connected, but APPS_WRITE was not detected."
                : "Connected and app discovery succeeded.";
            logger.LogInformation(
                "TrueNAS connection test {DiagnosticId} succeeded. ReadAccess={HasReadAccess} WriteAccess={HasWriteAccess}",
                diagnosticId,
                hasReadAccess,
                !rolesDetected || hasWriteAccess);
            return new ConnectionTestResult(true, writeMessage, true, !rolesDetected || hasWriteAccess, DiagnosticId: diagnosticId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var (code, message) = SanitizeException(exception);
            var diagnosticMessage = BuildDiagnosticMessage(message, code, diagnosticId);
            logger.LogError(
                exception,
                "TrueNAS connection test {DiagnosticId} failed at stage {Stage}. ErrorCode={ErrorCode} ErrorMessage={ErrorMessage}",
                diagnosticId,
                stage,
                code,
                message);
            await RecordConnectionResultAsync(false, code, diagnosticMessage, cancellationToken);
            return new ConnectionTestResult(false, diagnosticMessage, false, false, code, diagnosticId);
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
        await EnsureConnectedAsync(cancellationToken);
        try
        {
            _ = await SendRequestAsync<JsonElement>(
                "core.job_wait",
                [jobId],
                cancellationToken,
                disableTimeout: true);
        }
        catch (TrueNasClientException exception)
        {
            var diagnostic = await TryGetJobDiagnosticAsync(jobId, cancellationToken);
            if (diagnostic is not null)
            {
                throw new TrueNasClientException(exception.Code, diagnostic, exception);
            }

            throw;
        }
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

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken, string? diagnosticId = null)
    {
        var options = await settingsService.GetConnectionOptionsAsync(cancellationToken);
        diagnosticId ??= Guid.NewGuid().ToString("N");
        var fingerprint = string.Join(
            '|',
            options.ServerUri,
            options.Username,
            options.VerifyTls,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey))));

        if (transport?.State == WebSocketState.Open &&
            authenticated &&
            string.Equals(connectionFingerprint, fingerprint, StringComparison.Ordinal))
        {
            logger.LogDebug("Reusing authenticated TrueNAS connection for diagnostic {DiagnosticId}", diagnosticId);
            return;
        }

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (transport?.State == WebSocketState.Open &&
                authenticated &&
                string.Equals(connectionFingerprint, fingerprint, StringComparison.Ordinal))
            {
                logger.LogDebug("Reusing authenticated TrueNAS connection for diagnostic {DiagnosticId}", diagnosticId);
                return;
            }

            var endpoint = GetSafeEndpoint(options.ServerUri);
            logger.LogInformation(
                "TrueNAS connection attempt {DiagnosticId} started. Endpoint={Endpoint} Host={Host} Port={Port} VerifyTls={VerifyTls}",
                diagnosticId,
                endpoint,
                options.ServerUri.DnsSafeHost,
                options.ServerUri.Port,
                options.VerifyTls);
            await DisposeTransportAsync();
            transport = transportFactory.Create();
            receiveCancellation = new CancellationTokenSource();

            try
            {
                await transport.ConnectAsync(options, cancellationToken);
                logger.LogInformation(
                    "TrueNAS connection attempt {DiagnosticId} completed the WebSocket transport stage. State={State}",
                    diagnosticId,
                    transport.State);
                receiveTask = ReceiveLoopAsync(transport, receiveCancellation.Token);
                TrueNasAuthResponseDto auth;
                try
                {
                    logger.LogInformation(
                        "TrueNAS connection attempt {DiagnosticId} is authenticating with API_KEY_PLAIN",
                        diagnosticId);
                    auth = await SendRequestAsync<TrueNasAuthResponseDto>(
                        "auth.login_ex",
                        [
                            new
                            {
                                mechanism = "API_KEY_PLAIN",
                                username = options.Username,
                                api_key = options.ApiKey,
                                login_options = new { user_info = true }
                            }
                        ],
                        cancellationToken,
                        diagnosticId: diagnosticId);
                }
                catch (TrueNasClientException exception) when (exception.Code == "-32602")
                {
                    logger.LogWarning(
                        "TrueNAS connection attempt {DiagnosticId} rejected the username authentication shape; retrying the legacy API-key shape",
                        diagnosticId);
                    auth = await SendRequestAsync<TrueNasAuthResponseDto>(
                        "auth.login_ex",
                        [
                            new
                            {
                                mechanism = "API_KEY_PLAIN",
                                api_key = options.ApiKey,
                                login_options = new { user_info = true }
                            }
                        ],
                        cancellationToken,
                        diagnosticId: diagnosticId);
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
                logger.LogInformation(
                    "TrueNAS connection attempt {DiagnosticId} authenticated successfully. RolesDetected={RolesDetected} ReadAccess={HasReadAccess} WriteAccess={HasWriteAccess}",
                    diagnosticId,
                    rolesDetected,
                    hasReadAccess,
                    hasWriteAccess);
            }
            catch
            {
                await DisposeTransportAsync();
                throw;
            }
        }
        catch (WebSocketException exception)
        {
            var classified = ClassifyWebSocketException(exception);
            var diagnosticMessage = BuildDiagnosticMessage(classified.Message, classified.Code, diagnosticId);
            logger.LogError(
                exception,
                "TrueNAS connection attempt {DiagnosticId} failed during the WebSocket stage. WebSocketError={WebSocketError} NativeErrorCode={NativeErrorCode} ClassifiedErrorCode={ClassifiedErrorCode}",
                diagnosticId,
                exception.WebSocketErrorCode,
                exception.NativeErrorCode,
                classified.Code);
            throw new TrueNasClientException(classified.Code, diagnosticMessage, classified);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "TrueNAS connection attempt {DiagnosticId} failed during connection setup",
                diagnosticId);
            if (exception is TrueNasClientException clientException)
            {
                throw new TrueNasClientException(
                    clientException.Code,
                    BuildDiagnosticMessage(clientException.Message, clientException.Code, diagnosticId),
                    clientException);
            }

            throw;
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task<T> SendRequestAsync<T>(
        string method,
        object?[] parameters,
        CancellationToken cancellationToken,
        bool disableTimeout = false,
        string? diagnosticId = null)
    {
        var activeTransport = transport;
        if (activeTransport?.State != WebSocketState.Open)
        {
            throw new TrueNasClientException("NETWORK_ERROR", "The TrueNAS WebSocket is not connected.");
        }

        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PendingRequest(method, diagnosticId, completion);
        if (!pending.TryAdd(id, request))
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
            logger.LogDebug(
                "Sending TrueNAS RPC request {RequestId} for method {Method}. DiagnosticId={DiagnosticId}",
                id,
                method,
                diagnosticId ?? "none");
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                await activeTransport.SendAsync(payload, cancellationToken);
            }
            finally
            {
                sendGate.Release();
            }

            var result = disableTimeout
                ? await completion.Task.WaitAsync(cancellationToken)
                : await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            logger.LogDebug(
                "TrueNAS RPC request {RequestId} for method {Method} completed. DiagnosticId={DiagnosticId}",
                id,
                method,
                diagnosticId ?? "none");
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
                    !pending.TryGetValue(id, out var request))
                {
                    logger.LogDebug("Ignoring an uncorrelated TrueNAS WebSocket message");
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var rpcException = ParseRpcError(error);
                    if (string.Equals(request.Method, "auth.login_ex", StringComparison.Ordinal))
                    {
                        rpcException = new TrueNasClientException(
                            rpcException.Code,
                            "TrueNAS rejected the authentication request.",
                            rpcException);
                    }

                    logger.LogWarning(
                        "TrueNAS RPC request {RequestId} for method {Method} failed. DiagnosticId={DiagnosticId} ErrorCode={ErrorCode} ErrorMessage={ErrorMessage}",
                        id,
                        request.Method,
                        request.DiagnosticId ?? "none",
                        rpcException.Code,
                        rpcException.Message);
                    request.Completion.TrySetException(rpcException);
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    request.Completion.TrySetResult(result.Clone());
                }
                else
                {
                    logger.LogWarning(
                        "TrueNAS RPC request {RequestId} for method {Method} received a malformed response. DiagnosticId={DiagnosticId}",
                        id,
                        request.Method,
                        request.DiagnosticId ?? "none");
                    request.Completion.TrySetException(
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
            logger.LogError(
                exception,
                "TrueNAS WebSocket receive loop stopped unexpectedly. ErrorType={ErrorType} TransportState={TransportState} PendingRequests={PendingRequests}",
                exception.GetType().Name,
                activeTransport.State,
                pending.Count);
        }
        finally
        {
            authenticated = false;
            if (terminalError is null && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "TrueNAS WebSocket receive loop ended because the remote connection closed. TransportState={TransportState} PendingRequests={PendingRequests}",
                    activeTransport.State,
                    pending.Count);
            }

            var error = terminalError is null
                ? new TrueNasClientException("CONNECTION_CLOSED", "The TrueNAS connection closed.")
                : new TrueNasClientException("NETWORK_ERROR", "The TrueNAS connection was interrupted.", terminalError);
            foreach (var request in pending.Values)
            {
                request.Completion.TrySetException(error);
            }
        }
    }

    private async Task<string?> TryGetJobDiagnosticAsync(long jobId, CancellationToken cancellationToken)
    {
        try
        {
            var job = await CallAsync<JsonElement>(
                "core.get_jobs",
                [
                    new object[] { new object[] { "id", "=", jobId } },
                    new Dictionary<string, object?> { ["get"] = true }
                ],
                cancellationToken);
            if (job.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var state = job.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString()
                : "FAILED";
            var error = job.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            return Sanitize($"TrueNAS job {state}: {error ?? "No additional diagnostic was returned."}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Unable to retrieve diagnostic details for TrueNAS job {JobId}", jobId);
            return null;
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
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Ignoring an error while closing the previous TrueNAS WebSocket transport");
            }

            await oldTransport.DisposeAsync();
        }

        if (receiveTask is not null && receiveTask.Id != Task.CurrentId)
        {
            try
            {
                await receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "The previous TrueNAS WebSocket receive loop did not stop cleanly");
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
        var messages = GetExceptionMessages(exception);
        if (FindInnerException<AuthenticationException>(exception) is not null ||
            messages.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            messages.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            messages.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            return new TrueNasClientException("TLS_FAILURE", "TLS certificate validation or negotiation failed.", exception);
        }

        var socketException = FindInnerException<SocketException>(exception);
        if (socketException?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            return new TrueNasClientException(
                "DNS_FAILURE",
                "The configured TrueNAS hostname could not be resolved from the app container. Verify TRUENAS_WEBSOCKET_URL and any extra_hosts mapping, then redeploy.",
                exception);
        }

        if (socketException?.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            return new TrueNasClientException(
                "NETWORK_UNREACHABLE",
                "The app container has no route to the configured TrueNAS endpoint. Verify Host Network mode, TRUENAS_WEBSOCKET_URL, and the container logs.",
                exception);
        }

        if (socketException?.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new TrueNasClientException(
                "CONNECTION_REFUSED",
                "The configured TrueNAS endpoint refused the WebSocket connection. Verify its address and port and the TrueNAS web service, then redeploy if the host changed.",
                exception);
        }

        if (socketException?.SocketErrorCode == SocketError.TimedOut ||
            messages.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return new TrueNasClientException(
                "TIMEOUT",
                "The TrueNAS WebSocket connection timed out from the app container.",
                exception);
        }

        if (exception.WebSocketErrorCode == WebSocketError.NotAWebSocket)
        {
            return new TrueNasClientException(
                "WEBSOCKET_UPGRADE_FAILED",
                "TrueNAS did not accept the WebSocket upgrade. Verify the /api/current endpoint.",
                exception);
        }

        return new TrueNasClientException("NETWORK_ERROR", "Unable to connect to TrueNAS.", exception);
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

    private static string BuildDiagnosticMessage(string message, string code, string diagnosticId) =>
        message.Contains("Diagnostic ID:", StringComparison.Ordinal)
            ? Sanitize(message)
            : Sanitize($"{message} Error code: {code}. Diagnostic ID: {diagnosticId}. Check the container logs for this ID.");

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

    private static TException? FindInnerException<TException>(Exception exception) where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }

    private static string GetExceptionMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private sealed record PendingRequest(string Method, string? DiagnosticId, TaskCompletionSource<JsonElement> Completion);

    public async ValueTask DisposeAsync()
    {
        await ResetConnectionAsync();
        connectionGate.Dispose();
        sendGate.Dispose();
    }
}
