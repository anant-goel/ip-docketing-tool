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

    // --- Automatic pipeline state (phase 34) ---

    /// <summary>Where the downloaded PDF landed locally, once fetched.</summary>
    public string? LocalPdfPath { get; set; }

    /// <summary>Bytes on disk, so a truncated download is detectable without re-reading the file.</summary>
    public long PdfSizeBytes { get; set; }

    public DateTime? DownloadedUtc { get; set; }

    /// <summary>Set once text has been pulled out, whether by text layer or OCR.</summary>
    public DateTime? TextExtractedUtc { get; set; }

    /// <summary>"TextLayer", "Ocr", or "Failed" - recorded because OCR output needs more scepticism than a text layer.</summary>
    public string? ExtractionMethod { get; set; }

    /// <summary>How many published marks were parsed out of the extracted text.</summary>
    public int MarksParsed { get; set; }

    /// <summary>Set once the watch has been run against this issue, so it is never double-processed.</summary>
    public DateTime? WatchRunUtc { get; set; }

    /// <summary>Last error from the automatic pipeline, surfaced on the automation page.</summary>
    public string? LastError { get; set; }
}
