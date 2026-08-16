namespace IPDocketing.Core.Models;

/// <summary>
/// Records a status-update summary generated for a client. Generation and
/// text drafting are local (see ClientUpdateService) — there is no email
/// sending here. This exists to build the copy and let you paste it into
/// whatever you actually send from (Outlook, Gmail, etc.), and to keep a
/// record of what was sent and when.
/// </summary>
public class ClientUpdateLog
{
    public int Id { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string SummaryText { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; } = DateTime.UtcNow;
    public bool MarkedSent { get; set; }
    public DateTime? SentDate { get; set; }
}
