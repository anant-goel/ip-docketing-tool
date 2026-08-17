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

    /// <summary>
    /// docx section 8 - "an automated tool which will send automatic updates to
    /// the clients". The generation half is genuinely automatic: this runs every
    /// client whose last update is older than the interval and drafts a fresh
    /// one, with no one clicking anything. It is called on startup and can be
    /// called from the page.
    ///
    /// The sending half is not automatic and cannot be made so from here - there
    /// is no mail transport configured in this app. Use BuildMailtoUri to open
    /// the draft in the default mail client, or copy the text. Turning this into
    /// true unattended sending needs an SMTP host and credential (or a Microsoft
    /// Graph app registration) that only you can provision.
    /// </summary>
    public List<ClientUpdateLog> GenerateDueUpdates(TimeSpan interval)
    {
        var cutoff = DateTime.UtcNow - interval;
        var generated = new List<ClientUpdateLog>();

        foreach (var clientName in GetClientNames())
        {
            var last = _db.ClientUpdateLogs
                .Where(l => l.ClientName == clientName)
                .OrderByDescending(l => l.GeneratedDate)
                .FirstOrDefault();

            if (last is not null && last.GeneratedDate > cutoff) continue;
            generated.Add(GenerateUpdate(clientName));
        }

        return generated;
    }

    /// <summary>Generates a fresh update for every client, regardless of when the last one ran.</summary>
    public List<ClientUpdateLog> GenerateForAllClients() =>
        GetClientNames().Select(GenerateUpdate).ToList();

    /// <summary>
    /// Pre-filled mail draft for a generated update. The recipient is left blank
    /// because client contact addresses are not stored anywhere in this schema -
    /// deliberately, since that is client PII this app has no reason to hold.
    /// </summary>
    public string BuildMailtoUri(ClientUpdateLog log, string? recipientEmail = null)
    {
        var subject = $"Portfolio update - {log.ClientName} - {log.GeneratedDate:dd MMM yyyy}";
        var body = log.SummaryText;
        if (body.Length > 1800) body = body[..1800] + Environment.NewLine + "... (truncated - full text is in the app)";

        return $"mailto:{Uri.EscapeDataString(recipientEmail ?? string.Empty)}" +
               $"?subject={Uri.EscapeDataString(subject)}" +
               $"&body={Uri.EscapeDataString(body)}";
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
