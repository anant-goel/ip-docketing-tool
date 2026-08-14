namespace IPDocketing.Core.Models;

public class Document
{
    public int Id { get; set; }
    public int MatterId { get; set; }
    public Matter? Matter { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "General";
    public int Version { get; set; } = 1;

    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

    public string? OcrText { get; set; }
    public bool OcrProcessed { get; set; }
}
