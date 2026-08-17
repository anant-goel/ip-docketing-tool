namespace IPDocketing.Core.Models;

/// <summary>
/// docx section 4 — Trademark Watch. One row per potential-conflict pairing
/// between a mark you've logged from a Journal issue and one of your own
/// portfolio Matters, found by the phonetic/contains matcher in
/// WatchService. This is a local, portfolio-side filter, not a live IP-India
/// crawl -- it only runs against journal marks you (or a future connector)
/// have entered, so a JournalIssue with no logged marks yet produces no
/// alerts. See IIndiaIpSearchConnector for the piece that would eventually
/// pull journal mark listings automatically.
/// </summary>
public class WatchAlert
{
    public int Id { get; set; }

    public int JournalIssueId { get; set; }
    public JournalIssue? JournalIssue { get; set; }

    /// <summary>The conflicting mark as published in the journal.</summary>
    public string PublishedMark { get; set; } = string.Empty;
    public string? PublishedApplicant { get; set; }

    public int MatterId { get; set; }
    public Matter? Matter { get; set; }

    /// <summary>0-100 rough similarity score from the matcher, not a legal opinion.</summary>
    public int SimilarityScore { get; set; }

    /// <summary>
    /// Which signal drove the score - "spelling", "tokens", "phonetic",
    /// "containment", "ocr" or "identical". An alert that can't say why it
    /// fired is one nobody can check, and one everybody eventually ignores.
    /// </summary>
    public string? PrimarySignal { get; set; }

    /// <summary>Plain-English reasons, newline-separated, shown under the alert.</summary>
    public string? MatchExplanation { get; set; }

    /// <summary>Nice class of the published mark, where the Journal entry carried one.</summary>
    public string? PublishedClass { get; set; }

    /// <summary>Set when the published mark came from OCR rather than a text layer - it deserves more scepticism.</summary>
    public bool FromOcr { get; set; }

    public bool Dismissed { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
