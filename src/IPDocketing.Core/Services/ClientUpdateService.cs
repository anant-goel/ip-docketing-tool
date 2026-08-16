using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// Client update automation. "Automated" here means the *drafting* is
/// automatic — pulling every matter for a client plus its open deadlines
/// into a formatted summary — not that email gets sent on its own. Sending
/// requires wiring an SMTP/Graph/Gmail connector, which isn't done; this
/// produces the text and logs that it was generated/sent so you have a
/// record either way.
/// </summary>
public class ClientUpdateService
{
    private readonly AppDbContext _db;

    public ClientUpdateService(AppDbContext db)
    {
        _db = db;
    }

    public List<string> GetClientNames() =>
        _db.Matters.Select(m => m.ClientName).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct().OrderBy(c => c).ToList();

    public List<ClientUpdateLog> GetHistory(string? clientName = null)
    {
        var query = _db.ClientUpdateLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(clientName))
            query = query.Where(l => l.ClientName == clientName);
        return query.OrderByDescending(l => l.GeneratedDate).ToList();
    }

    /// <summary>Builds the summary text and saves it as a log entry (not yet marked sent).</summary>
    public ClientUpdateLog GenerateUpdate(string clientName)
    {
        var matters = _db.Matters
            .Include(m => m.Deadlines)
            .Where(m => m.ClientName == clientName)
            .OrderBy(m => m.MatterNumber)
            .ToList();

        var text = BuildSummary(clientName, matters);

        var log = new ClientUpdateLog
        {
            ClientName = clientName,
            SummaryText = text,
            GeneratedDate = DateTime.UtcNow
        };
        _db.ClientUpdateLogs.Add(log);
        _db.SaveChanges();
        return log;
    }

    public void MarkSent(int logId)
    {
        var log = _db.ClientUpdateLogs.Find(logId);
        if (log is null) return;
        log.MarkedSent = true;
        log.SentDate = DateTime.UtcNow;
        _db.SaveChanges();
    }

    private static string BuildSummary(string clientName, List<Matter> matters)
    {
        var lines = new List<string>
        {
            $"Portfolio update for {clientName}",
            $"Generated {DateTime.UtcNow:dd MMM yyyy}",
            ""
        };

        if (matters.Count == 0)
        {
            lines.Add("No matters currently on file for this client.");
            return string.Join(Environment.NewLine, lines);
        }

        foreach (var matter in matters)
        {
            lines.Add($"• {matter.MatterNumber} — {matter.Title} ({matter.Type}, {matter.Status})");

            var openDeadlines = matter.Deadlines
                .Where(d => d.Status == DeadlineStatus.Open)
                .OrderBy(d => d.DueDate)
                .ToList();

            if (openDeadlines.Count == 0)
            {
                lines.Add("   No open deadlines.");
            }
            else
            {
                foreach (var deadline in openDeadlines)
                    lines.Add($"   - Due {deadline.DueDate:dd MMM yyyy}: {deadline.Description}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
