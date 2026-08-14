namespace IPDocketing.Core.Models;

public class Matter
{
    public int Id { get; set; }
    public string MatterNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public MatterType Type { get; set; }
    public string Country { get; set; } = "US";
    public MatterStatus Status { get; set; } = MatterStatus.Pending;

    public DateTime? PriorityDate { get; set; }
    public DateTime? FilingDate { get; set; }
    public string? ApplicationNumber { get; set; }
    public string? PublicationNumber { get; set; }
    public string? GrantNumber { get; set; }

    public int? ParentMatterId { get; set; }
    public Matter? ParentMatter { get; set; }
    public List<Matter> ChildMatters { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public List<Event> Events { get; set; } = new();
    public List<Deadline> Deadlines { get; set; } = new();
    public List<Document> Documents { get; set; } = new();
}
