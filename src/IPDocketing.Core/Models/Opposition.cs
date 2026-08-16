namespace IPDocketing.Core.Models;

/// <summary>
/// docx section 3 — Opposition Management Database. Tracks both directions
/// (filed by us / filed against our marks) in one table, distinguished by
/// <see cref="Direction"/>, since the docx explicitly asks for both
/// categorized under one opposition register.
/// </summary>
public class Opposition
{
    public int Id { get; set; }

    /// <summary>Optional link to the Matter this opposition concerns, when it's one of our own filings.</summary>
    public int? MatterId { get; set; }
    public Matter? Matter { get; set; }

    public OppositionDirection Direction { get; set; }
    public OppositionStatus Status { get; set; } = OppositionStatus.Open;

    /// <summary>The trademark application/registration number the opposition concerns.</summary>
    public string TrademarkNumber { get; set; } = string.Empty;
    public string MarkDetails { get; set; } = string.Empty;

    /// <summary>The other side — opponent if FiledAgainstUs is us being opposed by them; applicant if FiledByUs.</summary>
    public string OpposingParty { get; set; } = string.Empty;

    public DateTime? NoticeDate { get; set; }
    public DateTime? CounterStatementDueDate { get; set; }
    public DateTime? HearingDate { get; set; }

    public int? AssignedToId { get; set; }
    public TeamMember? AssignedTo { get; set; }

    public List<Document> Documents { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
