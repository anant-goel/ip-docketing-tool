using System.Net;
using System.Text;
using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// docx section 5 - "Trademark Status Tracker, including Opposition Status".
///
/// Assembles one mark's complete prosecution and opposition history into a
/// single object: current status, every logged event, every deadline (open and
/// closed) with its nominal/operative pair, every filed document grouped by the
/// categories the spec names, and every opposition touching the mark in either
/// direction.
///
/// The spec also asks for "a tool to print the status and documents". That is
/// implemented as <see cref="BuildPrintableHtml"/>, which renders a
/// self-contained HTML sheet the caller writes to disk and opens - the browser
/// then owns the print dialog, page setup and PDF export. WinUI 3's
/// PrintManager needs an MSIX identity and a HWND interop dance that this app,
/// which is deliberately unpackaged, does not have; routing through the default
/// browser gets a real, working Print button today instead of a broken one.
/// </summary>
public class StatusTrackerService
{
    private readonly AppDbContext _db;

    public StatusTrackerService(AppDbContext db)
    {
        _db = db;
    }

    public sealed record StatusDossier(
        Matter Matter,
        List<Event> Events,
        List<Deadline> Deadlines,
        List<Document> Documents,
        List<Opposition> Oppositions)
    {
        public IEnumerable<IGrouping<string, Document>> DocumentsByCategory =>
            Documents.GroupBy(d => d.DocumentType ?? DocumentTypes.General)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        public Deadline? NextDeadline =>
            Deadlines.Where(d => d.Status is DeadlineStatus.Open or DeadlineStatus.Extended)
                     .OrderBy(d => d.DueDate)
                     .FirstOrDefault();

        public bool HasOpenOpposition =>
            Oppositions.Any(o => o.Status is not (OppositionStatus.Decided
                                              or OppositionStatus.Withdrawn
                                              or OppositionStatus.Settled));
    }

    /// <summary>Returns null when the matter id no longer exists.</summary>
    public StatusDossier? GetDossier(int matterId)
    {
        var matter = _db.Matters
            .Include(m => m.AssignedTo)
            .Include(m => m.ParentMatter)
            .FirstOrDefault(m => m.Id == matterId);

        if (matter is null) return null;

        var events = _db.Events
            .Where(e => e.MatterId == matterId)
            .OrderByDescending(e => e.EventDate)
            .ToList();

        var deadlines = _db.Deadlines
            .Include(d => d.CountryRule)
            .Where(d => d.MatterId == matterId)
            .OrderBy(d => d.DueDate)
            .ToList();

        var documents = _db.Documents
            .Where(d => d.MatterId == matterId)
            .OrderByDescending(d => d.UploadedDate)
            .ToList();

        // An opposition is linked to this mark either through MatterId or,
        // where it was logged straight off the TMR portal without being tied to
        // an internal matter, by the trademark/application number itself.
        var applicationNumber = matter.ApplicationNumber;
        var oppositions = _db.Oppositions
            .Include(o => o.AssignedTo)
            .Include(o => o.Documents)
            .Where(o => o.MatterId == matterId ||
                        (applicationNumber != null && applicationNumber != "" &&
                         o.TrademarkNumber == applicationNumber))
            .OrderByDescending(o => o.CreatedDate)
            .ToList();

        // Documents attached to those oppositions belong in the mark's history
        // too - the spec asks for opposition documents on the status page.
        foreach (var opposition in oppositions)
            documents.AddRange(opposition.Documents.Where(d => d.MatterId != matterId));

        return new StatusDossier(matter, events, deadlines, documents, oppositions);
    }

    /// <summary>
    /// Plain-text version, for the clipboard and for pasting into an email.
    /// </summary>
    public string BuildPlainText(StatusDossier dossier)
    {
        var m = dossier.Matter;
        var sb = new StringBuilder();

        sb.AppendLine($"TRADEMARK STATUS - {m.Title}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Matter number     : {m.MatterNumber}");
        sb.AppendLine($"Application number: {Or(m.ApplicationNumber)}");
        sb.AppendLine($"Registration no.  : {Or(m.GrantNumber)}");
        sb.AppendLine($"Proprietor        : {Or(m.ProprietorName)}");
        sb.AppendLine($"Class             : {Or(m.NiceClass)}");
        sb.AppendLine($"Mark type         : {(m.MarkType?.ToString() ?? "-")}");
        sb.AppendLine($"Attorney of record: {Or(m.AttorneyOfRecord)}");
        sb.AppendLine($"State             : {Or(m.State)}");
        sb.AppendLine($"Jurisdiction      : {m.Country}");
        sb.AppendLine($"Filing date       : {Date(m.FilingDate)}");
        sb.AppendLine($"Registration date : {Date(m.RegistrationDate)}");
        sb.AppendLine($"Renewal due       : {Date(m.RenewalDueDate)}");
        sb.AppendLine($"Current status    : {m.Status}");
        if (!string.IsNullOrWhiteSpace(m.PortalAlert))
            sb.AppendLine($"Portal alert      : {m.PortalAlert}");
        sb.AppendLine($"Assigned to       : {m.AssignedTo?.Name ?? "Unassigned"}");
        sb.AppendLine();

        sb.AppendLine("PROSECUTION HISTORY");
        sb.AppendLine(new string('-', 60));
        if (dossier.Events.Count == 0) sb.AppendLine("No events logged.");
        foreach (var e in dossier.Events)
            sb.AppendLine($"{e.EventDate:dd MMM yyyy}  {e.Type,-16} {e.Notes}");
        sb.AppendLine();

        sb.AppendLine("DEADLINES");
        sb.AppendLine(new string('-', 60));
        if (dossier.Deadlines.Count == 0) sb.AppendLine("No deadlines recorded.");
        foreach (var d in dossier.Deadlines)
            sb.AppendLine($"{d.DueDate:dd MMM yyyy}  {d.Status,-10} {d.Description} " +
                          $"(nominal {d.NominalDueDate:dd MMM yyyy})");
        sb.AppendLine();

        sb.AppendLine("OPPOSITION STATUS");
        sb.AppendLine(new string('-', 60));
        if (dossier.Oppositions.Count == 0) sb.AppendLine("No opposition proceedings on record.");
        foreach (var o in dossier.Oppositions)
        {
            var direction = o.Direction == OppositionDirection.FiledByUs ? "Filed by us" : "Filed against us";
            sb.AppendLine($"{o.TrademarkNumber} - {direction} - {o.Status}");
            sb.AppendLine($"    Opposing party  : {Or(o.OpposingParty)}");
            sb.AppendLine($"    Notice date     : {Date(o.NoticeDate)}");
            sb.AppendLine($"    Counter-stmt due: {Date(o.CounterStatementDueDate)}");
            sb.AppendLine($"    Hearing         : {Date(o.HearingDate)}");
        }
        sb.AppendLine();

        sb.AppendLine("DOCUMENTS ON FILE");
        sb.AppendLine(new string('-', 60));
        if (dossier.Documents.Count == 0) sb.AppendLine("No documents filed.");
        foreach (var group in dossier.DocumentsByCategory)
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var doc in group.OrderByDescending(d => d.UploadedDate))
                sb.AppendLine($"    {doc.UploadedDate:dd MMM yyyy}  v{doc.Version}  {doc.FileName}");
        }
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:dd MMM yyyy HH:mm} from the local docket.");

        return sb.ToString();
    }

    /// <summary>
    /// Self-contained printable HTML - no external CSS, no fonts to fetch, so it
    /// prints identically offline. Auto-opens the browser print dialog when
    /// <paramref name="autoPrint"/> is true.
    /// </summary>
    public string BuildPrintableHtml(StatusDossier dossier, bool autoPrint = true)
    {
        var m = dossier.Matter;
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>Status - {H(m.Title)}</title>");
        sb.AppendLine(@"<style>
:root{color-scheme:light}
*{box-sizing:border-box}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;margin:0;padding:32px 40px;color:#14181f;background:#fff;font-size:13px;line-height:1.5}
h1{font-size:20px;margin:0 0 2px}
h2{font-size:13px;text-transform:uppercase;letter-spacing:.09em;color:#5b6577;margin:26px 0 8px;padding-bottom:5px;border-bottom:1px solid #d8dde6}
.sub{color:#6b7484;margin:0 0 20px}
table{border-collapse:collapse;width:100%;margin:0 0 6px}
th,td{text-align:left;padding:6px 9px;border-bottom:1px solid #e6eaf0;vertical-align:top}
th{background:#f4f6fa;font-weight:600;color:#3d4658;white-space:nowrap}
.facts td:first-child{width:190px;color:#5b6577;white-space:nowrap}
.pill{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11px;font-weight:600;border:1px solid #c9d2e0;background:#f2f5fa}
.alert{border-color:#f0b7b2;background:#fdeceb;color:#a3241b}
.ok{border-color:#a9e0c0;background:#eafaf1;color:#1c7a45}
.warn{border-color:#f2d3a0;background:#fdf4e5;color:#8a5a10}
.empty{color:#8a92a1;font-style:italic;padding:6px 2px}
footer{margin-top:34px;padding-top:10px;border-top:1px solid #d8dde6;color:#8a92a1;font-size:11px}
@media print{body{padding:0}h2{break-after:avoid}tr{break-inside:avoid}}
</style>");
        if (autoPrint)
            sb.AppendLine("<script>window.addEventListener('load',function(){setTimeout(function(){window.print();},250);});</script>");
        sb.AppendLine("</head><body>");

        sb.AppendLine($"<h1>{H(m.Title)}</h1>");
        sb.AppendLine($"<p class=\"sub\">{H(m.MatterNumber)} &middot; {H(m.ClientName)} &middot; {H(m.Country)}</p>");

        sb.AppendLine("<h2>Current status</h2><table class=\"facts\">");
        Fact(sb, "Application number", m.ApplicationNumber);
        Fact(sb, "Registration number", m.GrantNumber);
        Fact(sb, "Proprietor", m.ProprietorName);
        Fact(sb, "Class", m.NiceClass);
        Fact(sb, "Mark type", m.MarkType?.ToString());
        Fact(sb, "Attorney of record", m.AttorneyOfRecord);
        Fact(sb, "State", m.State);
        Fact(sb, "Filing date", Date(m.FilingDate));
        Fact(sb, "Registration date", Date(m.RegistrationDate));
        Fact(sb, "Renewal due", Date(m.RenewalDueDate));
        Fact(sb, "Assigned to", m.AssignedTo?.Name ?? "Unassigned");
        var statusClass = m.Status switch
        {
            MatterStatus.Granted or MatterStatus.Active => "ok",
            MatterStatus.Abandoned or MatterStatus.Expired => "alert",
            _ => "warn"
        };
        sb.AppendLine($"<tr><td>Status</td><td><span class=\"pill {statusClass}\">{H(m.Status.ToString())}</span></td></tr>");
        if (!string.IsNullOrWhiteSpace(m.PortalAlert))
            sb.AppendLine($"<tr><td>Portal alert</td><td><span class=\"pill alert\">{H(m.PortalAlert)}</span></td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Prosecution history</h2>");
        if (dossier.Events.Count == 0) sb.AppendLine("<p class=\"empty\">No events logged.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>Date</th><th>Event</th><th>Notes</th></tr>");
            foreach (var e in dossier.Events)
                sb.AppendLine($"<tr><td>{e.EventDate:dd MMM yyyy}</td><td>{H(e.Type.ToString())}</td><td>{H(e.Notes)}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Deadlines</h2>");
        if (dossier.Deadlines.Count == 0) sb.AppendLine("<p class=\"empty\">No deadlines recorded.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>Operative due</th><th>Nominal due</th><th>Description</th><th>Status</th><th>Rule</th></tr>");
            foreach (var d in dossier.Deadlines)
                sb.AppendLine($"<tr><td>{d.DueDate:dd MMM yyyy}</td><td>{d.NominalDueDate:dd MMM yyyy}</td>" +
                              $"<td>{H(d.Description)}</td><td>{H(d.Status.ToString())}</td>" +
                              $"<td>{H(d.CountryRule?.Citation ?? d.RuleVersionApplied)}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Opposition status</h2>");
        if (dossier.Oppositions.Count == 0) sb.AppendLine("<p class=\"empty\">No opposition proceedings on record.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>TM number</th><th>Direction</th><th>Opposing party</th>" +
                          "<th>Status</th><th>Notice</th><th>Counter-statement due</th><th>Hearing</th></tr>");
            foreach (var o in dossier.Oppositions)
            {
                var direction = o.Direction == OppositionDirection.FiledByUs ? "Filed by us" : "Filed against us";
                sb.AppendLine($"<tr><td>{H(o.TrademarkNumber)}</td><td>{H(direction)}</td>" +
                              $"<td>{H(o.OpposingParty)}</td><td>{H(o.Status.ToString())}</td>" +
                              $"<td>{Date(o.NoticeDate)}</td><td>{Date(o.CounterStatementDueDate)}</td>" +
                              $"<td>{Date(o.HearingDate)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Documents on file</h2>");
        if (dossier.Documents.Count == 0) sb.AppendLine("<p class=\"empty\">No documents filed.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>Category</th><th>Filed</th><th>Ver.</th><th>File</th><th>Path</th></tr>");
            foreach (var group in dossier.DocumentsByCategory)
                foreach (var doc in group.OrderByDescending(d => d.UploadedDate))
                    sb.AppendLine($"<tr><td>{H(group.Key)}</td><td>{doc.UploadedDate:dd MMM yyyy}</td>" +
                                  $"<td>{doc.Version}</td><td>{H(doc.FileName)}</td><td>{H(doc.FilePath)}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine($"<footer>Generated {DateTime.Now:dd MMM yyyy HH:mm} from the local IP Docketing database. " +
                      "Reflects what has been recorded locally - it is not a live read of the TMR portal.</footer>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static void Fact(StringBuilder sb, string label, string? value) =>
        sb.AppendLine($"<tr><td>{H(label)}</td><td>{H(string.IsNullOrWhiteSpace(value) ? "-" : value)}</td></tr>");

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Date(DateTime? value) => value?.ToString("dd MMM yyyy") ?? "-";
}
