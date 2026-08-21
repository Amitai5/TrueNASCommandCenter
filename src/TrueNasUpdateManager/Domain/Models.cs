namespace TrueNasUpdateManager.Domain;

internal static class TrueNasConnectionDefaults
{
    public const string ServerUrl = "wss://127.0.0.1/api/current";
    public static Uri ServerUri { get; } = new(ServerUrl, UriKind.Absolute);
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

public sealed record ScheduleValidationResult(
    bool IsValid,
    string? Error,
    IReadOnlyList<DateTimeOffset> NextRuns,
    string Preview);

public sealed record SecretInput(string? Value)
{
    public bool HasNewValue => !string.IsNullOrWhiteSpace(Value);
}
