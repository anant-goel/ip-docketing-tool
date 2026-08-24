using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// docx section 4 — Trademark Watch. Runs a local similarity match between
/// marks published in a journal issue (entered manually today, since no
/// live IP-India feed is connected yet — see IIndiaIpSearchConnector) and
/// your own portfolio, and records the ones worth a human look.
/// </summary>
public class WatchService
{
    private readonly AppDbContext _db;
    private const int AlertThreshold = 60;

    private readonly MarkSimilarityService _similarity = new();

    public WatchService(AppDbContext db)
    {
        _db = db;
    }

    public List<WatchAlert> GetAll() =>
        _db.WatchAlerts.Include(w => w.Matter).Include(w => w.JournalIssue)
            .Where(w => !w.Dismissed)
            .OrderByDescending(w => w.SimilarityScore)
            .ToList();

    /// <summary>
    /// Runs the watch over a set of published marks.
    ///
    /// Phase 35: scoring moved to <see cref="MarkSimilarityService"/>, which
    /// replaces a single raw edit-distance number with five explainable signals
    /// plus class weighting. The practical effect is fewer alerts and better
    /// ones: "SUPER FOODS" against "SUPER TOOLS" no longer fires on a shared
    /// generic word, while "SHUBH LAXMI" against "SHUBH LAXMI FOODS PVT LTD"
    /// and "KWIK BRITE" against "QUICK BRIGHT" now do - and those are the ones
    /// that used to slip through.
    ///
    /// Every alert records which signal fired and why, because a score with no
    /// reasoning behind it is one a reviewer can neither trust nor check.
    /// </summary>
    public List<WatchAlert> RunWatch(int journalIssueId, IEnumerable<(string Mark, string? Applicant)> publishedMarks) =>
        RunWatch(journalIssueId, publishedMarks.Select(p => (p.Mark, p.Applicant, (string?)null)), fromOcr: false);

    /// <summary>
    /// Full form, carrying the published class and whether the text came from
    /// OCR. Class lets an alert be weighted for proximity of goods; the OCR flag
    /// enables confusion-tolerant matching and lowers the confidence shown.
    /// </summary>
    public List<WatchAlert> RunWatch(
        int journalIssueId,
        IEnumerable<(string Mark, string? Applicant, string? NiceClass)> publishedMarks,
        bool fromOcr)
    {
        var portfolio = _db.Matters.Where(m => m.Type == MatterType.Trademark).ToList();
        var created = new List<WatchAlert>();

        // Pre-normalising the portfolio once, rather than inside the inner
        // loop, turns an O(published x portfolio) run from unbearable into
        // instant on a real portfolio - a 400-page issue against 500 marks is
        // hundreds of thousands of comparisons.
        // The result of this was previously thrown away: the loop below destructured
        // it as `(matter, _)` and then handed Compare the RAW title, which
        // normalised it again - once per published mark, for every matter. The
        // pre-computation the comment describes was real work with no effect.
        // MarkSimilarityService.Prepare now carries the core, tokens and phonetic
        // key too, and Compare has an overload that consumes them.
        var prepared = portfolio
            .Select(m => (Matter: m, Mark: MarkSimilarityService.Prepare(m.Title)))
            .Where(x => !x.Mark.IsEmpty)
            .ToList();

        // Alerts already raised for this issue, so a re-run never duplicates.
        //
        // BUG FIX: this used to project `w.PublishedMark + "|" + w.MatterId`
        // inside the LINQ query. C# compiles `string + int` to
        // String.Concat(object, object), which EF Core cannot translate to SQL,
        // so the whole method threw "The LINQ expression could not be
        // translated" the moment a watch was run - which is exactly the
        // "TM watch does nothing" symptom. The two columns are now pulled back
        // first and the key is built in memory, where string concatenation is
        // just string concatenation.
        //
        // Keys are also compared case-insensitively and on the trimmed mark, so
        // "SUN RISE" and "Sun Rise " no longer produce two alerts for the same
        // pairing across re-runs.
        var existing = _db.WatchAlerts
            .Where(w => w.JournalIssueId == journalIssueId)
            .Select(w => new { w.PublishedMark, w.PublishedApplicant, w.PublishedClass, w.MatterId })
            .AsEnumerable()
            .Select(w => AlertKey(w.PublishedMark, w.PublishedApplicant, w.PublishedClass, w.MatterId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (mark, applicant, niceClass) in publishedMarks)
        {
            if (string.IsNullOrWhiteSpace(mark)) continue;

            // Prepared once per published mark, then reused across the whole
            // portfolio - the other half of the fix described above.
            var published = MarkSimilarityService.Prepare(mark);
            if (published.IsEmpty) continue;

            foreach (var (matter, prepMatter) in prepared)
            {
                var result = _similarity.Compare(published, prepMatter, fromOcr);

                // Cheap reject before the class work. The floor is 52, not 55:
                // ApplyClassWeighting can only ever ADD 8, and the alert
                // threshold is 60, so 52 is the arithmetically correct cut. At
                // 55 a same-class pair scoring 52-54 was discarded although it
                // would have alerted at 60-62.
                if (result.Score < AlertThreshold - 8) continue;

                var (score, classNote) = _similarity.ApplyClassWeighting(
                    result.Score, niceClass, matter.NiceClass);

                if (score < AlertThreshold) continue;

                var key = AlertKey(mark, applicant, niceClass, matter.Id);
                if (!existing.Add(key)) continue;

                var reasons = new List<string>(result.Reasons);
                if (classNote is not null) reasons.Add(classNote);
                if (fromOcr) reasons.Add("Published mark was read by OCR - verify against the Journal PDF.");

                var alert = new WatchAlert
                {
                    JournalIssueId = journalIssueId,
                    PublishedMark = mark,
                    PublishedApplicant = applicant,
                    PublishedClass = niceClass,
                    MatterId = matter.Id,
                    SimilarityScore = score,
                    PrimarySignal = result.PrimarySignal,
                    MatchExplanation = string.Join(Environment.NewLine, reasons),
                    FromOcr = fromOcr
                };

                _db.WatchAlerts.Add(alert);
                created.Add(alert);
            }
        }

        if (created.Count > 0) _db.SaveChanges();
        return created;
    }

    /// <summary>
    /// Identity of one alert. Built in memory, never inside a LINQ-to-SQL query.
    ///
    /// THE APPLICANT AND CLASS ARE PART OF THE IDENTITY, and leaving them out
    /// was silently losing alerts. The key used to be (mark, matter), but one
    /// journal issue routinely publishes the same word for several different
    /// applicants in several classes - AMRIT filed by Bhatia Foods in class 30
    /// and AMRIT filed by Verma Agro in class 5 are two separate applications,
    /// each opposable in its own right. Under the old key the second one hit
    /// `existing.Add` returning false and was skipped with no alert, no log line
    /// and no counter, so the opposition window could lapse with nothing on
    /// record that it had ever been seen. Because the same key was also the
    /// persisted cross-run dedup, re-running the issue could not recover it.
    ///
    /// Interior whitespace is collapsed as well as trimmed, so "SUN RISE" and
    /// "SUN  RISE" - the same mark, one of them read by OCR - are one alert
    /// rather than two.
    /// </summary>
    private static string AlertKey(string? publishedMark, string? applicant, string? niceClass, int matterId) =>
        string.Join('|',
            Collapse(publishedMark),
            Collapse(applicant),
            Collapse(niceClass),
            matterId.ToString());

    private static string Collapse(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public List<WatchAlert> GetAllIncludingDismissed() =>
        _db.WatchAlerts.Include(w => w.Matter).Include(w => w.JournalIssue)
            .OrderByDescending(w => w.CreatedDate)
            .ToList();

    /// <summary>
    /// docx section 7 asks the watch to "generate reports" weekly. This renders
    /// the live alerts as a printable HTML sheet grouped by client, so one
    /// document can be reviewed, printed, or saved per week. Passing a client
    /// name narrows it to that client's portfolio, which is what you want when
    /// the report is going out to them rather than being reviewed internally.
    /// </summary>
    public string BuildWatchReportHtml(string? clientName = null, bool autoPrint = true)
    {
        var alerts = GetAll();
        if (!string.IsNullOrWhiteSpace(clientName))
            alerts = alerts.Where(a => a.Matter is not null &&
                string.Equals(a.Matter.ClientName, clientName, StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>Trademark watch report - {System.Net.WebUtility.HtmlEncode(clientName ?? "all clients")}</title>");
        sb.AppendLine(@"<style>
:root{color-scheme:light}
body{font-family:'Segoe UI',system-ui,sans-serif;margin:0;padding:32px 40px;color:#14181f;background:#fff;font-size:13px;line-height:1.5}
h1{font-size:20px;margin:0 0 2px}
h2{font-size:13px;text-transform:uppercase;letter-spacing:.09em;color:#5b6577;margin:26px 0 8px;padding-bottom:5px;border-bottom:1px solid #d8dde6}
.sub{color:#6b7484;margin:0 0 20px}
table{border-collapse:collapse;width:100%}
th,td{text-align:left;padding:6px 9px;border-bottom:1px solid #e6eaf0;vertical-align:top}
th{background:#f4f6fa;font-weight:600;color:#3d4658}
.score{font-weight:700}
.high{color:#a3241b}.mid{color:#8a5a10}
.empty{color:#8a92a1;font-style:italic}
footer{margin-top:34px;padding-top:10px;border-top:1px solid #d8dde6;color:#8a92a1;font-size:11px}
@media print{body{padding:0}tr{break-inside:avoid}}
</style>");
        if (autoPrint)
            sb.AppendLine("<script>window.addEventListener('load',function(){setTimeout(function(){window.print();},250);});</script>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>Trademark watch report</h1>");
        sb.AppendLine($"<p class=\"sub\">{System.Net.WebUtility.HtmlEncode(clientName ?? "All clients")} &middot; generated {DateTime.Now:dd MMM yyyy}</p>");

        if (alerts.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">No open watch alerts. Nothing published in the journal issues processed so far scored above the similarity threshold against this portfolio.</p>");
        }
        else
        {
            foreach (var group in alerts
                .GroupBy(a => a.Matter?.ClientName ?? "Unlinked")
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"<h2>{System.Net.WebUtility.HtmlEncode(group.Key)}</h2>");
                sb.AppendLine("<table><tr><th>Published mark</th><th>Applicant</th><th>Conflicts with</th>" +
                              "<th>Why it matched</th><th>Journal issue</th><th>Score</th></tr>");
                foreach (var alert in group.OrderByDescending(a => a.SimilarityScore))
                {
                    var cssClass = alert.SimilarityScore >= 80 ? "high" : "mid";
                    sb.AppendLine("<tr>" +
                        $"<td>{System.Net.WebUtility.HtmlEncode(alert.PublishedMark)}</td>" +
                        $"<td>{System.Net.WebUtility.HtmlEncode(alert.PublishedApplicant ?? "-")}</td>" +
                        $"<td>{System.Net.WebUtility.HtmlEncode(alert.Matter?.Title ?? "-")}</td>" +
                        $"<td>{System.Net.WebUtility.HtmlEncode(alert.MatchExplanation ?? "-").Replace("\n", "<br/>")}" +
                        (alert.FromOcr ? "<br/><em>Read by OCR - verify against the PDF.</em>" : "") + "</td>" +
                        $"<td>{System.Net.WebUtility.HtmlEncode(alert.JournalIssue?.IssueNumber ?? "-")}</td>" +
                        $"<td class=\"score {cssClass}\">{alert.SimilarityScore}%</td></tr>");
                }
                sb.AppendLine("</table>");
            }
        }

        sb.AppendLine("<footer>Scores combine spelling, word-set, phonetic and containment signals computed locally, " +
                      "weighted for class proximity. The strongest signal is named against each row. This is a shortlist " +
                      "for human review, <strong>not</strong> a likelihood-of-confusion opinion - that turns on goods, " +
                      "trade channels, distinctiveness and reputation, none of which a string comparison can see. " +
                      "Rows marked as read by OCR should be checked against the Journal PDF before being relied on.</footer>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>CSV of the same data, for pushing into a spreadsheet.</summary>
    public string BuildWatchReportCsv(string? clientName = null)
    {
        var alerts = GetAll();
        if (!string.IsNullOrWhiteSpace(clientName))
            alerts = alerts.Where(a => a.Matter is not null &&
                string.Equals(a.Matter.ClientName, clientName, StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new System.Text.StringBuilder("Client,PublishedMark,PublishedApplicant,ConflictsWith,MatterNumber,JournalIssue,PublicationDate,Score,Signal,WhyItMatched,FromOcr\n");
        foreach (var a in alerts.OrderByDescending(a => a.SimilarityScore))
        {
            sb.AppendLine(string.Join(',',
                Csv(a.Matter?.ClientName),
                Csv(a.PublishedMark),
                Csv(a.PublishedApplicant),
                Csv(a.Matter?.Title),
                Csv(a.Matter?.MatterNumber),
                Csv(a.JournalIssue?.IssueNumber),
                Csv(a.JournalIssue?.PublicationDate.ToString("yyyy-MM-dd")),
                a.SimilarityScore.ToString(),
                Csv(a.PrimarySignal),
                Csv((a.MatchExplanation ?? "").Replace(Environment.NewLine, "; ")),
                a.FromOcr ? "yes" : "no"));
        }
        return sb.ToString();
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    public void Dismiss(int alertId)
    {
        var alert = _db.WatchAlerts.Find(alertId);
        if (alert is null) return;
        alert.Dismissed = true;
        _db.SaveChanges();
    }

    // The old single-signal scorer and its full-matrix Levenshtein used to sit
    // here. Both were dead - every score comes from MarkSimilarityService.Compare -
    // and leaving a second, weaker implementation of the same idea in the file is
    // an invitation to call the wrong one.
}
