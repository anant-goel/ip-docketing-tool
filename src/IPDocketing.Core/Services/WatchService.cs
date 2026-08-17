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
        var prepared = portfolio
            .Select(m => (Matter: m, Normalized: MarkSimilarityService.Normalize(m.Title)))
            .Where(x => x.Normalized.Length > 0)
            .ToList();

        // Alerts already raised for this issue, so a re-run never duplicates.
        var existing = _db.WatchAlerts
            .Where(w => w.JournalIssueId == journalIssueId)
            .Select(w => w.PublishedMark + "|" + w.MatterId)
            .ToHashSet();

        foreach (var (mark, applicant, niceClass) in publishedMarks)
        {
            if (string.IsNullOrWhiteSpace(mark)) continue;

            foreach (var (matter, _) in prepared)
            {
                var result = _similarity.Compare(mark, matter.Title, fromOcr);
                if (result.Score < 55) continue; // cheap reject before class work

                var (score, classNote) = _similarity.ApplyClassWeighting(
                    result.Score, niceClass, matter.NiceClass);

                if (score < AlertThreshold) continue;

                var key = mark + "|" + matter.Id;
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

    /// <summary>
    /// Normalized Levenshtein similarity (0-100). A plain edit-distance
    /// score, not a trademark "likelihood of confusion" test -- treat
    /// results as a shortlist to review, not a legal conclusion.
    /// </summary>
    private static int SimilarityScore(string a, string b)
    {
        a = a.Trim().ToUpperInvariant();
        b = b.Trim().ToUpperInvariant();
        if (a.Length == 0 || b.Length == 0) return 0;

        var distance = Levenshtein(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return (int)Math.Round((1.0 - (double)distance / maxLen) * 100);
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }
}
