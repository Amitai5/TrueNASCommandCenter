using System.ComponentModel.DataAnnotations;

namespace TrueNasCommandCenter.Domain;

/// <summary>Records an operator or system transition for an operations inbox item.</summary>
public sealed class OperationsInboxHistoryRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InboxItemId { get; set; }
    public OperationsInboxItem InboxItem { get; set; } = null!;
    public OperationsInboxHistoryAction Action { get; set; }
    public DateTime TimestampUtc { get; set; }

    [MaxLength(128)]
    public string Actor { get; set; } = "System";

    [MaxLength(1024)]
    public string Message { get; set; } = string.Empty;
}
