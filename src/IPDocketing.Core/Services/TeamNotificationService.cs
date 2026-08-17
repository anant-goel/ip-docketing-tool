using System.Text;
using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// docx section 1 - "Automated tool which sends notification to internal team
/// members for approaching deadlines".
///
/// What is automatic: finding every approaching or overdue deadline, resolving
/// who owns it (matter assignee first, then the deadline's ResponsibleUser
/// name, then unassigned), grouping it per person, and drafting their digest.
/// That runs on a timer with no one asking for it.
///
/// What is NOT automatic, and I am not going to pretend otherwise: actually
/// delivering the mail. There is no SMTP server, no Graph token and no service
/// account in this app, so nothing can leave the machine on its own. Each
/// digest is emitted with a ready-to-launch mailto: URI the caller can open in
/// the default mail client, plus the plain text for pasting. Wiring real
/// unattended sending needs an SMTP host + credential (or a Graph app
/// registration) that only you can create.
///
/// Windows toast reminders are separate and DO fire on their own - see
/// MainWindow.RefreshReminders. Those are local to whoever is at this machine,
/// which is why the per-person digest exists alongside them.
/// </summary>
public class TeamNotificationService
{
    private readonly AppDbContext _db;

    /// <summary>Deadlines this many days out or closer are treated as "approaching".</summary>
    public int LookAheadDays { get; set; } = 14;

    public TeamNotificationService(AppDbContext db)
    {
        _db = db;
    }

    public sealed record DeadlineNotice(
        int DeadlineId,
        string MatterNumber,
        string MatterTitle,
        string Description,
        DateTime DueDate,
        int DaysRemaining)
    {
        public bool IsOverdue => DaysRemaining < 0;

        public string UrgencyLabel => DaysRemaining switch
        {
            < 0 => $"OVERDUE by {Math.Abs(DaysRemaining)} day(s)",
            0 => "DUE TODAY",
            1 => "due tomorrow",
            _ => $"due in {DaysRemaining} days"
        };
    }

    public sealed record TeamDigest(
        int? TeamMemberId,
        string RecipientName,
        string? RecipientEmail,
        List<DeadlineNotice> Notices)
    {
        public int OverdueCount => Notices.Count(n => n.IsOverdue);
        public int ApproachingCount => Notices.Count(n => !n.IsOverdue);
        public bool CanEmail => !string.IsNullOrWhiteSpace(RecipientEmail);
    }

    /// <summary>
    /// Builds one digest per person who owns at least one overdue or approaching
    /// deadline. People with a clean sheet are omitted entirely - a digest that
    /// says "nothing to do" trains everyone to stop reading them.
    /// </summary>
    public List<TeamDigest> BuildDigests(DateTime? asOf = null)
    {
        var today = (asOf ?? DateTime.Today).Date;
        var horizon = today.AddDays(LookAheadDays);

        var deadlines = _db.Deadlines
            .Include(d => d.Matter)
            .ThenInclude(m => m!.AssignedTo)
            .Where(d => d.Status == DeadlineStatus.Open || d.Status == DeadlineStatus.Extended)
            .ToList()
            .Where(d => d.DueDate.Date <= horizon)
            .OrderBy(d => d.DueDate)
            .ToList();

        var members = _db.TeamMembers.ToList();

        var buckets = new Dictionary<string, TeamDigestBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var deadline in deadlines)
        {
            var assignee = deadline.Matter?.AssignedTo;

            // Fall back to the free-text ResponsibleUser on the deadline, matched
            // against the team list by name so the digest can still reach them.
            if (assignee is null && !string.IsNullOrWhiteSpace(deadline.ResponsibleUser))
            {
                assignee = members.FirstOrDefault(t =>
                    string.Equals(t.Name, deadline.ResponsibleUser, StringComparison.OrdinalIgnoreCase));
            }

            var key = assignee?.Id.ToString()
                      ?? (string.IsNullOrWhiteSpace(deadline.ResponsibleUser)
                          ? "__unassigned__"
                          : "name:" + deadline.ResponsibleUser.Trim());

            if (!buckets.TryGetValue(key, out var builder))
            {
                builder = new TeamDigestBuilder
                {
                    TeamMemberId = assignee?.Id,
                    Name = assignee?.Name
                           ?? (string.IsNullOrWhiteSpace(deadline.ResponsibleUser)
                               ? "Unassigned"
                               : deadline.ResponsibleUser.Trim()),
                    Email = assignee?.Email
                };
                buckets[key] = builder;
            }

            builder.Notices.Add(new DeadlineNotice(
                deadline.Id,
                deadline.Matter?.MatterNumber ?? "-",
                deadline.Matter?.Title ?? "-",
                deadline.Description,
                deadline.DueDate,
                (deadline.DueDate.Date - today).Days));
        }

        return buckets.Values
            .Select(b => new TeamDigest(b.TeamMemberId, b.Name, b.Email,
                b.Notices.OrderBy(n => n.DueDate).ToList()))
            // Overdue first, then whoever has most on their plate, then unassigned last.
            .OrderByDescending(d => d.OverdueCount)
            .ThenByDescending(d => d.Notices.Count)
            .ToList();
    }

    public string BuildDigestText(TeamDigest digest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Deadline notification for {digest.RecipientName}");
        sb.AppendLine($"Generated {DateTime.Now:dd MMM yyyy HH:mm}");
        sb.AppendLine();

        if (digest.OverdueCount > 0)
        {
            sb.AppendLine($"OVERDUE ({digest.OverdueCount})");
            foreach (var notice in digest.Notices.Where(n => n.IsOverdue))
                sb.AppendLine($"  - {notice.DueDate:dd MMM yyyy}  {notice.MatterNumber}  {notice.Description}  [{notice.UrgencyLabel}]");
            sb.AppendLine();
        }

        if (digest.ApproachingCount > 0)
        {
            sb.AppendLine($"APPROACHING - next {LookAheadDays} days ({digest.ApproachingCount})");
            foreach (var notice in digest.Notices.Where(n => !n.IsOverdue))
                sb.AppendLine($"  - {notice.DueDate:dd MMM yyyy}  {notice.MatterNumber}  {notice.Description}  [{notice.UrgencyLabel}]");
            sb.AppendLine();
        }

        sb.AppendLine("Sent from the IP Docketing desktop app.");
        return sb.ToString();
    }

    /// <summary>
    /// A mailto: URI the caller can hand to the shell to open a pre-filled draft
    /// in the default mail client. Returns null when the person has no address
    /// on file. Body length is capped because Windows truncates very long
    /// mailto: URIs silently - the full text is always available separately.
    /// </summary>
    public string? BuildMailtoUri(TeamDigest digest)
    {
        if (!digest.CanEmail) return null;

        var subject = digest.OverdueCount > 0
            ? $"[IP Docket] {digest.OverdueCount} overdue, {digest.ApproachingCount} approaching"
            : $"[IP Docket] {digest.ApproachingCount} deadline(s) approaching";

        var body = BuildDigestText(digest);
        if (body.Length > 1800) body = body[..1800] + "\r\n... (truncated - see the app for the full list)";

        return $"mailto:{Uri.EscapeDataString(digest.RecipientEmail!)}" +
               $"?subject={Uri.EscapeDataString(subject)}" +
               $"&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>Single-line summary for the dashboard strip.</summary>
    public string BuildSummaryLine(IReadOnlyList<TeamDigest> digests)
    {
        if (digests.Count == 0) return "No approaching deadlines assigned to anyone.";
        var overdue = digests.Sum(d => d.OverdueCount);
        var approaching = digests.Sum(d => d.ApproachingCount);
        return $"{digests.Count} team member(s) to notify - {overdue} overdue, {approaching} approaching.";
    }

    private sealed class TeamDigestBuilder
    {
        public int? TeamMemberId { get; init; }
        public string Name { get; init; } = "Unassigned";
        public string? Email { get; init; }
        public List<DeadlineNotice> Notices { get; } = new();
    }
}
