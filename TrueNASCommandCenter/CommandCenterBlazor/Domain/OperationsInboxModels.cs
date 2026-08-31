namespace TrueNasCommandCenter.Domain;

/// <summary>Defines filters applied to operations inbox queries.</summary>
public sealed record OperationsInboxQuery(
    string? Search = null,
    OperationsInboxStatus? Status = null,
    OperationsInboxSource? Source = null,
    OperationsInboxSeverity? Severity = null,
    DateTime? SinceUtc = null,
    int Limit = 250);

/// <summary>Contains inbox items and counts for the current query.</summary>
public sealed record OperationsInboxSnapshot(
    IReadOnlyList<OperationsInboxItem> Items,
    int OpenCount,
    int CriticalCount,
    int AcknowledgedCount,
    int ResolvedCount,
    DateTime? LastObservedUtc);

/// <summary>Reports a unified inbox refresh outcome without discarding successful sources.</summary>
public sealed record OperationsInboxRefreshResult(int ObservedCount, int ChangedCount, IReadOnlyList<string> Warnings);
