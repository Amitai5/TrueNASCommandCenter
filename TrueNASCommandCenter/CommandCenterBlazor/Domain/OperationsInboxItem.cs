using System.ComponentModel.DataAnnotations;

namespace TrueNasCommandCenter.Domain;

/// <summary>Represents one durable incident or activity item in the unified operations inbox.</summary>
public sealed class OperationsInboxItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(512)]
    public string Fingerprint { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? CorrelationGroup { get; set; }

    public OperationsInboxSource Source { get; set; }
    public OperationsInboxKind Kind { get; set; }
    public OperationsInboxSeverity Severity { get; set; }
    public OperationsInboxStatus Status { get; set; } = OperationsInboxStatus.Open;

    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string Summary { get; set; } = string.Empty;

    [MaxLength(4096)]
    public string? Details { get; set; }

    [MaxLength(256)]
    public string? SourceReference { get; set; }

    [MaxLength(256)]
    public string? RelatedAppId { get; set; }

    [MaxLength(1024)]
    public string DeepLink { get; set; } = "/inbox";

    public DateTime OccurredUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public bool IsSourceActive { get; set; }
    public double? ProgressPercent { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public OperationsInboxPushState PushState { get; set; }
    public DateTime? PushAttemptedUtc { get; set; }

    [MaxLength(512)]
    public string? PushError { get; set; }

    public ICollection<OperationsInboxHistoryRecord> History { get; set; } = [];
}
