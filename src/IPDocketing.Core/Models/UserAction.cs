namespace IPDocketing.Core.Models;

/// <summary>
/// Immutable, hash-chained audit-trail record. Rows are only ever inserted,
/// never updated or deleted, to satisfy malpractice/compliance audit
/// requirements. Each record hashes its own payload together with the prior
/// record's hash (RecordHash = SHA256(PriorHash + payload)), so the ledger
/// is tamper-evident end to end: altering any historical row breaks every
/// subsequent hash and is immediately detectable.
/// </summary>
public class UserAction
{
    public int Id { get; set; }
    public string UserName { get; set; } = "local.user";
    public string ActionType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? Details { get; set; }

    public string? PriorHash { get; set; }
    public string RecordHash { get; set; } = string.Empty;
}
