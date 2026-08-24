namespace TrueNasAppManager.Domain;

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
    string? DiagnosticId = null);

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

public sealed record AppManagementResult(bool Success, string Message, string? State = null, string? ErrorCode = null);

public sealed record InventoryRefreshResult(int Discovered, int Missing, IReadOnlyList<string> AppIds);

public sealed record AppHealthEvaluationResult(int Checked, int IncidentsOpened, int Recovered, int RestartAttempts);

public sealed record AppWebUiLinks(string? LocalUrl, string? RemoteUrl, string? SelectedUrl, WebUiRoute SelectedRoute);

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
