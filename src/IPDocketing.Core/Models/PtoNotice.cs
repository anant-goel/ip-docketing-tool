namespace IPDocketing.Core.Models;

public class PtoNotice
{
    public int Id { get; set; }
    public int? MatterId { get; set; }
    public Matter? Matter { get; set; }

    public PtoSource Source { get; set; }
    public string NoticeType { get; set; } = string.Empty;
    public DateTime ReceivedDate { get; set; } = DateTime.UtcNow;
    public string? RawContent { get; set; }
    public bool Processed { get; set; }
    public int? LinkedDeadlineId { get; set; }
}
