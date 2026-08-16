namespace IPDocketing.Core.Models;

public class Document
{
    public int Id { get; set; }

    // Nullable now: a document can belong to a Matter OR an Opposition
    // (docx section 3 asks for documents attached to opposition records too).
    public int? MatterId { get; set; }
    public Matter? Matter { get; set; }

    public int? OppositionId { get; set; }
    public Opposition? Opposition { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "General";
    public int Version { get; set; } = 1;

    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

    public string? OcrText { get; set; }
    public bool OcrProcessed { get; set; }
}
