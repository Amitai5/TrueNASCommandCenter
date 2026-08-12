namespace TrueNasUpdateManager.Domain;

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
    CheckNow,
    CheckAndUpdateNow,
    UpdateNow,
    Rollback
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
    Rollback
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
    ManualApprovalAvailable,
    AutomaticUpdateFailed,
    AutomaticUpdateBlocked,
    RollbackOccurred,
    AutomaticUpdateSucceeded,
    ScheduledCheckFailed,
    TrueNasConnectionFailed
}

public enum NotificationProvider
{
    Email,
    Webhook
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
