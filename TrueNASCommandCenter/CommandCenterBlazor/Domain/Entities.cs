using System.ComponentModel.DataAnnotations;

namespace TrueNasCommandCenter.Domain;

public sealed class AppRecord
{
    [Key]
    [MaxLength(256)]
    public string Id { get; set; } = string.Empty;

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    public bool IsCustom { get; set; }
    public bool IsInstalled { get; set; } = true;

    [MaxLength(32)]
    public string State { get; set; } = "STOPPED";

    [MaxLength(128)]
    public string? InstalledVersion { get; set; }

    [MaxLength(256)]
    public string? HumanVersion { get; set; }

    [MaxLength(128)]
    public string? LatestVersion { get; set; }

    [MaxLength(256)]
    public string? LatestHumanVersion { get; set; }

    public bool CatalogUpdateAvailable { get; set; }
    public bool ImageUpdateAvailable { get; set; }
    public string? OutdatedImagesJson { get; set; }
    public bool ActionRequired { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public DateTime? LastSuccessfulUpdateUtc { get; set; }
    public AppPolicy? Policy { get; set; }
    public VersionScope VersionScope { get; set; } = VersionScope.AnyVersion;
    public bool SnapshotHostPaths { get; set; }
    public bool? NotifySuccessOverride { get; set; }
    public bool NotifyOnDowntime { get; set; }
    public bool DowntimeNotificationActive { get; set; }
    public DowntimeAction DowntimeAction { get; set; }
    public AppHealthState HealthState { get; set; } = AppHealthState.Unknown;
    public bool MaintenanceMode { get; set; }
    public bool IsFavorite { get; set; }

    [MaxLength(64)]
    public string? GroupName { get; set; }

    public Guid? HealthIncidentId { get; set; }
    public DateTime? LastHealthCheckUtc { get; set; }
    public DateTime? RecoveryAttemptedUtc { get; set; }
    public DateTime? MissingSinceUtc { get; set; }

    [MaxLength(512)]
    public string? HealthMessage { get; set; }

    [MaxLength(2048)]
    public string? ManualPortalUrl { get; set; }

    [MaxLength(2048)]
    public string? LocalPortalUrl { get; set; }

    [MaxLength(2048)]
    public string? RemotePortalUrl { get; set; }

    [MaxLength(2048)]
    public string? Description { get; set; }

    [MaxLength(2048)]
    public string? HomeUrl { get; set; }

    [MaxLength(2048)]
    public string? IconUrl { get; set; }

    [MaxLength(128)]
    public string? Train { get; set; }

    public string? SourceUrlsJson { get; set; }

    [MaxLength(64)]
    public string StatusLabel { get; set; } = "Up to date";

    [MaxLength(512)]
    public string? StatusMessage { get; set; }

    public ICollection<UpdateAttempt> Attempts { get; set; } = [];
    public ICollection<AppPortRecord> Ports { get; set; } = [];
    public ICollection<AppPortalRecord> Portals { get; set; } = [];
    public ICollection<AppContainerRecord> Containers { get; set; } = [];
    public ICollection<UptimeKumaMonitorRecord> UptimeKumaMonitors { get; set; } = [];
}

public sealed class UptimeKumaMonitorRecord
{
    [Key]
    [MaxLength(128)]
    public string MonitorId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? AppId { get; set; }

    public AppRecord? App { get; set; }

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Type { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Url { get; set; }

    [MaxLength(512)]
    public string? Hostname { get; set; }

    public int? Port { get; set; }
    public UptimeKumaMonitorStatus Status { get; set; } = UptimeKumaMonitorStatus.Unknown;
    public double? ResponseTimeMilliseconds { get; set; }
    public double? UptimeRatio1Day { get; set; }
    public double? UptimeRatio30Days { get; set; }
    public double? UptimeRatio365Days { get; set; }
    public double? AverageResponseTimeMilliseconds1Day { get; set; }
    public double? AverageResponseTimeMilliseconds30Days { get; set; }
    public double? AverageResponseTimeMilliseconds365Days { get; set; }
    public bool? CertificateIsValid { get; set; }
    public double? CertificateDaysRemaining { get; set; }
    public bool IsPresent { get; set; } = true;
    public DateTime LastSeenUtc { get; set; }
}

public sealed class AppPortRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(256)]
    public string AppId { get; set; } = string.Empty;
    public AppRecord App { get; set; } = null!;
    [MaxLength(256)]
    public string? ContainerName { get; set; }
    [MaxLength(128)]
    public string? HostIp { get; set; }
    public int HostPort { get; set; }
    public int? ContainerPort { get; set; }
    [MaxLength(16)]
    public string Protocol { get; set; } = "tcp";
}

public sealed class AppPortalRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(256)]
    public string AppId { get; set; } = string.Empty;
    public AppRecord App { get; set; } = null!;
    [MaxLength(256)]
    public string Name { get; set; } = "Web UI";
    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;
}

public sealed class AppContainerRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(256)]
    public string AppId { get; set; } = string.Empty;
    public AppRecord App { get; set; } = null!;
    [MaxLength(256)]
    public string ContainerId { get; set; } = string.Empty;
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Image { get; set; }
    [MaxLength(32)]
    public string State { get; set; } = "UNKNOWN";
    public string? NetworksJson { get; set; }
    public string? VolumesJson { get; set; }
}

public sealed class GitHubRepositoryCache
{
    [Key]
    [MaxLength(2048)]
    public string RepositoryUrl { get; set; } = string.Empty;
    [MaxLength(256)]
    public string? ETag { get; set; }
    [MaxLength(512)]
    public string? FullName { get; set; }
    [MaxLength(2048)]
    public string? Description { get; set; }
    [MaxLength(128)]
    public string? License { get; set; }
    public int? Stars { get; set; }
    public string? TopicsJson { get; set; }
    public DateTime? LastFetchedUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    [MaxLength(1024)]
    public string? LastError { get; set; }
}

public sealed class UpdateRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public RunTrigger Trigger { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;
    public int CheckedCount { get; set; }
    public int EligibleCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }

    [MaxLength(1024)]
    public string? ErrorSummary { get; set; }

    public ICollection<UpdateAttempt> Attempts { get; set; } = [];
}

public sealed class UpdateAttempt
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public UpdateRun Run { get; set; } = null!;

    [MaxLength(256)]
    public string AppId { get; set; } = string.Empty;
    public AppRecord App { get; set; } = null!;
    public AttemptKind Kind { get; set; }

    [MaxLength(128)]
    public string? FromVersion { get; set; }

    [MaxLength(128)]
    public string? ToVersion { get; set; }

    public string? OutdatedImagesJson { get; set; }
    public AppPolicy? PolicyAtExecution { get; set; }
    public VersionScope? ScopeAtExecution { get; set; }
    public bool SnapshotRequested { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public AttemptStatus Status { get; set; } = AttemptStatus.Pending;

    [MaxLength(128)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string ReasonMessage { get; set; } = string.Empty;

    public long? TrueNasJobId { get; set; }

    [MaxLength(32)]
    public string? TrueNasJobState { get; set; }

    [MaxLength(2048)]
    public string? ErrorDetails { get; set; }
}

public sealed class NotificationRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; } = Guid.NewGuid();
    public NotificationEventType EventType { get; set; }

    [MaxLength(256)]
    public string? AppId { get; set; }

    [MaxLength(1024)]
    public string DeduplicationKey { get; set; } = string.Empty;

    public NotificationProvider Provider { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int? HttpStatusCode { get; set; }

    [MaxLength(1024)]
    public string? ErrorSummary { get; set; }
}

public sealed class WebPushSubscriptionRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(4096)]
    public string Endpoint { get; set; } = string.Empty;

    [MaxLength(256)]
    public string P256dh { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Auth { get; set; } = string.Empty;

    public DateTime? ExpirationUtc { get; set; }

    [MaxLength(128)]
    public string? DeviceName { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public int ConsecutiveFailures { get; set; }

    [MaxLength(512)]
    public string? LastError { get; set; }
}

public sealed class SettingsRecord
{
    [Key]
    public int Id { get; set; } = 1;
    public bool OnboardingCompleted { get; set; }
    public int OnboardingStep { get; set; } = 1;

    [MaxLength(2048)]
    public string? TrueNasUrl { get; set; }

    [MaxLength(256)]
    public string? TrueNasUsername { get; set; }

    public string? TrueNasApiKeyEncrypted { get; set; }
    public bool VerifyTls { get; set; } = true;
    public bool AllowInsecureWebSocket { get; set; }
    public DateTime? LastConnectionSuccessUtc { get; set; }

    [MaxLength(128)]
    public string? LastConnectionErrorCode { get; set; }

    [MaxLength(512)]
    public string? LastConnectionError { get; set; }

    public bool SchedulerEnabled { get; set; }

    [MaxLength(256)]
    public string? CronExpression { get; set; }

    [MaxLength(128)]
    public string? TimeZoneId { get; set; }

    public DateTime? LastScheduledRunUtc { get; set; }
    public DateTime? LastCompletedCheckUtc { get; set; }
    public DateTime? LastInventoryRefreshUtc { get; set; }

    public bool NotifyManualApproval { get; set; }
    public bool NotifyAutomaticFailure { get; set; }
    public bool NotifyAutomaticBlocked { get; set; }
    public bool NotifyRollback { get; set; }
    public bool NotifyAutomaticSuccess { get; set; }
    public bool NotifyScheduledCheckFailure { get; set; }
    public bool NotifyConnectionFailure { get; set; }

    public bool EmailEnabled { get; set; }

    [MaxLength(512)]
    public string? SmtpHost { get; set; }

    public int? SmtpPort { get; set; }
    public SmtpSecurity? SmtpSecurity { get; set; }

    [MaxLength(256)]
    public string? SmtpUsername { get; set; }

    public string? SmtpPasswordEncrypted { get; set; }

    [MaxLength(256)]
    public string? EmailFromName { get; set; }

    [MaxLength(320)]
    public string? EmailFromAddress { get; set; }

    public string? EmailRecipientsJson { get; set; }

    [MaxLength(2048)]
    public string? PortalHostOverride { get; set; }

    public bool GitHubEnrichmentEnabled { get; set; }

    public bool WebhookEnabled { get; set; }

    [MaxLength(2048)]
    public string? WebhookUrl { get; set; }

    public string? WebhookAuthorizationEncrypted { get; set; }
    public string? WebhookHeadersEncrypted { get; set; }
    public int WebhookTimeoutSeconds { get; set; } = 10;
    public int VerificationTimeoutSeconds { get; set; } = 300;
    public int ConnectionFailureCooldownMinutes { get; set; } = 360;
    public int? HistoryRetentionDays { get; set; }

    [MaxLength(256)]
    public string? ManagerAppId { get; set; }

    public bool UptimeKumaEnabled { get; set; }

    [MaxLength(2048)]
    public string? UptimeKumaBaseUrl { get; set; }

    [MaxLength(2048)]
    public string? UptimeKumaBrowserUrl { get; set; }

    public string? UptimeKumaApiKeyEncrypted { get; set; }
    public bool UptimeKumaVerifyTls { get; set; } = true;
    public int UptimeKumaRefreshIntervalSeconds { get; set; } = 60;
    public DateTime? LastUptimeKumaSyncUtc { get; set; }
    public DateTime? LastUptimeKumaSuccessUtc { get; set; }

    [MaxLength(1024)]
    public string? LastUptimeKumaError { get; set; }

    [MaxLength(256)]
    public string? WebPushPublicKey { get; set; }

    public string? WebPushPrivateKeyEncrypted { get; set; }
}
