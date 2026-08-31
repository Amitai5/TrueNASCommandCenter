namespace TrueNasCommandCenter.Domain;

public sealed record TrueNasEndpointOptions
{
    private TrueNasEndpointOptions(Uri serverUri)
    {
        ServerUri = serverUri;
    }

    public Uri ServerUri { get; }
    public string ServerUrl => ServerUri.AbsoluteUri;

    /// <summary>Parses and validates the deployment-configured TrueNAS WebSocket endpoint.</summary>
    /// <param name="value">The absolute WSS URL ending in /api/current.</param>
    /// <returns>The validated and normalized endpoint options.</returns>
    public static TrueNasEndpointOptions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("TRUENAS_WEBSOCKET_URL is required. Set it to the TrueNAS Web UI host followed by /api/current.", nameof(value));
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var serverUri) ||
            !string.Equals(serverUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(serverUri.Host) ||
            !string.IsNullOrEmpty(serverUri.UserInfo) ||
            !string.IsNullOrEmpty(serverUri.Query) ||
            !string.IsNullOrEmpty(serverUri.Fragment) ||
            !string.Equals(serverUri.AbsolutePath.TrimEnd('/'), "/api/current", StringComparison.Ordinal))
        {
            throw new ArgumentException("TRUENAS_WEBSOCKET_URL must be an absolute wss:// URL ending in /api/current without credentials, a query, or a fragment.", nameof(value));
        }

        var normalizedUri = new UriBuilder(serverUri)
        {
            Path = "/api/current",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        return new TrueNasEndpointOptions(normalizedUri);
    }
}

public sealed record ConnectionOptions(
    Uri ServerUri,
    string Username,
    string ApiKey,
    bool VerifyTls);

public sealed record ConnectionTestResult(
    bool Success,
    string Message,
    bool HasReadAccess,
    bool HasWriteAccess,
    string? ErrorCode = null,
    string? DiagnosticId = null)
{
    /// <summary>Gets the effective TrueNAS roles reported for the authenticated API session.</summary>
    public IReadOnlyList<string> AvailableRoles { get; init; } = [];
}

public sealed record VersionParts(int Major, int Minor, int Patch);

public sealed record UpdateDecision(
    UpdateDecisionKind Kind,
    string ReasonCode,
    string Message,
    string? TargetVersion = null);

public sealed record RunResult(
    Guid? RunId,
    RunStatus Status,
    string Message,
    int Checked = 0,
    int Succeeded = 0,
    int Failed = 0,
    int Skipped = 0);

public sealed record NotificationEvent(
    Guid EventId,
    NotificationEventType EventType,
    DateTime TimestampUtc,
    string DeduplicationKey,
    string Subject,
    string Message,
    string ReasonCode,
    string? AppId = null,
    string? AppName = null,
    string? InstalledVersion = null,
    string? AvailableVersionOrImages = null);

public sealed record NotificationDeliveryResult(bool Success, int? HttpStatusCode = null, string? Error = null);

/// <summary>Contains the browser-generated subscription material required to register one device for Web Push.</summary>
public sealed record WebPushSubscriptionInput(
    string Endpoint,
    string P256dh,
    string Auth,
    DateTimeOffset? ExpirationTime,
    string? DeviceName,
    string? UserAgent)
{
    /// <inheritdoc />
    public override string ToString() => $"WebPushSubscriptionInput {{ DeviceName = {DeviceName ?? "Browser device"}, HasEndpoint = {!string.IsNullOrWhiteSpace(Endpoint)} }}";
}

/// <summary>Describes Web Push capability, permission, and the current browser subscription.</summary>
public sealed record WebPushBrowserState(
    bool Supported,
    bool SecureContext,
    string Permission,
    WebPushSubscriptionInput? Subscription,
    string? Error = null);

/// <summary>Describes one registered browser without exposing its subscription encryption keys.</summary>
public sealed record WebPushSubscriptionSummary(
    Guid Id,
    string DeviceName,
    DateTime CreatedUtc,
    DateTime LastSeenUtc,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    int ConsecutiveFailures);

/// <summary>Provides the VAPID identity and registered devices used for a push delivery operation.</summary>
public sealed record WebPushDeliveryConfiguration(
    string PublicKey,
    string PrivateKey,
    IReadOnlyList<WebPushSubscriptionRecord> Subscriptions)
{
    /// <inheritdoc />
    public override string ToString() => $"WebPushDeliveryConfiguration {{ SubscriptionCount = {Subscriptions.Count} }}";
}

/// <summary>Contains a standards-based Web Push request before encryption and transport.</summary>
public sealed record WebPushProtocolRequest(
    string Endpoint,
    string VapidPublicKey,
    string VapidPrivateKey,
    int TimeToLiveSeconds = 3600)
{
    /// <inheritdoc />
    public override string ToString() => $"WebPushProtocolRequest {{ EndpointHost = {(Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ? endpoint.Host : "invalid")}, TimeToLiveSeconds = {TimeToLiveSeconds} }}";
}

public sealed record AppManagementResult(bool Success, string Message, string? State = null, string? ErrorCode = null);

public sealed record InventoryRefreshResult(int Discovered, int Missing, IReadOnlyList<string> AppIds);

public sealed record AppHealthEvaluationResult(int Checked, int IncidentsOpened, int Recovered, int RestartAttempts);

public sealed record AppWebUiLinks(string? LocalUrl, string? RemoteUrl, string? SelectedUrl, WebUiRoute SelectedRoute);

/// <summary>Represents one live TrueNAS resource sample for an installed application.</summary>
public sealed record AppResourceUsage(
    string AppId,
    int CpuUsagePercent,
    long MemoryBytes,
    long NetworkReceiveBytesPerSecond,
    long NetworkTransmitBytesPerSecond,
    long BlockReadBytes,
    long BlockWriteBytes,
    DateTimeOffset ObservedAtUtc);

/// <summary>Represents display-safe health and capacity information for one TrueNAS storage pool.</summary>
public sealed record StoragePoolHealth(
    string Name,
    string Status,
    bool IsHealthy,
    bool HasWarning,
    string? StatusDetail,
    long? SizeBytes,
    long? AllocatedBytes,
    long? FreeBytes,
    string? Fragmentation)
{
    public double? UsedPercentage => SizeBytes is > 0 && AllocatedBytes is not null
        ? Math.Clamp((double)AllocatedBytes.Value / SizeBytes.Value * 100, 0, 100)
        : null;
}

/// <summary>Represents the optional storage-pool dashboard state.</summary>
public sealed record StoragePoolOverview(
    IReadOnlyList<StoragePoolHealth> Pools,
    bool RequiresPoolRead = false,
    string? Error = null)
{
    public bool IsAvailable => !RequiresPoolRead && string.IsNullOrWhiteSpace(Error);
}

public sealed record UptimeKumaMonitorMetric(
    string MonitorId,
    string Name,
    string Type,
    string? Url,
    string? Hostname,
    int? Port,
    UptimeKumaMonitorStatus Status,
    double? ResponseTimeMilliseconds,
    double? UptimeRatio1Day,
    double? UptimeRatio30Days,
    double? UptimeRatio365Days,
    double? AverageResponseTimeMilliseconds1Day,
    double? AverageResponseTimeMilliseconds30Days,
    double? AverageResponseTimeMilliseconds365Days,
    bool? CertificateIsValid,
    double? CertificateDaysRemaining);

public sealed record UptimeKumaConnectionTestResult(bool Success, string Message, int MonitorCount = 0);

public sealed record UptimeKumaSyncResult(bool Success, string Message, int MonitorCount = 0);

public sealed record ConfigurationBackupFile(string FileName, string Json, bool IncludesSecrets);

public sealed record ConfigurationBackupInspection(int SchemaVersion, DateTimeOffset ExportedAtUtc, string ApplicationVersion, bool IncludesSecrets, int? AppCount);

public sealed record ConfigurationBackupPreview(int SchemaVersion, DateTimeOffset ExportedAtUtc, string ApplicationVersion, bool IncludesSecrets, int AppCount);

public sealed record ConfigurationRestoreResult(int AppsRestored, bool SecretsRestored, bool ConnectionReady);

public sealed record ScheduleValidationResult(
    bool IsValid,
    string? Error,
    IReadOnlyList<DateTimeOffset> NextRuns,
    string Preview);

public sealed record SecretInput(string? Value)
{
    public bool HasNewValue => !string.IsNullOrWhiteSpace(Value);
}
