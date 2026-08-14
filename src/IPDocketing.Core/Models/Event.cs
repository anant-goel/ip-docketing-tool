namespace IPDocketing.Core.Models;

public class Event
{
    public int Id { get; set; }
    public int MatterId { get; set; }
    public Matter? Matter { get; set; }

    public EventType Type { get; set; }
    public DateTime EventDate { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public List<Deadline> Deadlines { get; set; } = new();
}
