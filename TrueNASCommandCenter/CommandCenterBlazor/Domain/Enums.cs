namespace TrueNasCommandCenter.Domain;

public enum AppPolicy
{
    AutoUpdate,
    NotifyOnly,
    Ignore
}

public enum VersionScope
{
    AnyVersion,
    MinorAndPatch,
    PatchOnly
}

public enum RunTrigger
{
    Scheduled,
    RefreshApps,
    CheckNow,
    CheckAndUpdateNow,
    UpdateNow,
    Rollback,
    Lifecycle,
    HealthRecovery
}

public enum RunStatus
{
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Skipped,
    Cancelled
}

public enum AttemptKind
{
    CatalogUpgrade,
    ImageRefresh,
    Rollback,
    LifecycleStart,
    LifecycleStop,
    LifecycleRestart,
    AutomaticRecovery
}

public enum AttemptStatus
{
    Pending,
    Running,
    Verifying,
    Succeeded,
    Failed,
    Skipped,
    Blocked,
    Cancelled
}

public enum NotificationEventType
{
    AppDowntime,
    AppRecoverySucceeded,
    AppRecoveryFailed,
    ManualApprovalAvailable,
    AutomaticUpdateFailed,
    AutomaticUpdateBlocked,
    RollbackOccurred,
    AutomaticUpdateSucceeded,
    ScheduledCheckFailed,
    TrueNasConnectionFailed
}

public enum AppLifecycleAction
{
    Start,
    Stop,
    Restart
}

public enum AppManagementOrigin
{
    Manual,
    AutomaticRecovery
}

public enum DowntimeAction
{
    Ignore,
    NotifyOnly,
    RestartAndNotify
}

public enum AppHealthState
{
    Unknown,
    Running,
    Degraded,
    Stopped,
    Maintenance
}

public enum WebUiRoute
{
    Local,
    Remote
}

public enum UptimeKumaMonitorStatus
{
    Unknown,
    Down,
    Up,
    Pending,
    Maintenance
}

public enum NotificationProvider
{
    Email,
    Webhook,
    Push
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    Failed,
    SkippedDuplicate
}

public enum SmtpSecurity
{
    None,
    StartTls,
    Tls
}

public enum UpdateDecisionKind
{
    NoUpdate,
    Eligible,
    Notify,
    Ignored,
    Unconfigured,
    ManualApproval,
    Blocked
}
