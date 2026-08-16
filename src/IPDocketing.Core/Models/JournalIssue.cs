namespace IPDocketing.Core.Models;

/// <summary>
/// docx section 5 — Trademark Journal Monitoring. The TMR Journal is
/// published weekly as a PDF; IP-India does not expose it via API, so this
/// stores the issue metadata + link you record each week rather than a
/// live feed. Pairs with a manual or connector-driven watch process to flag
/// marks similar to the portfolio (see IIndiaIpSearchConnector).
/// </summary>
public class JournalIssue
{
    public int Id { get; set; }
    public string IssueNumber { get; set; } = string.Empty;
    public DateTime PublicationDate { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool Reviewed { get; set; }
    public string? Notes { get; set; }
}
