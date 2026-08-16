using System.Net.Http;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace IPDocketing.Core.Services;

/// <summary>
/// Fetches the public Trademark Journal listing at
/// search.ipindia.gov.in/IPOJournal/Journal/Trademark - unlike the
/// trademark/patent SEARCH portals, this specific page has no login, OTP,
/// or CAPTCHA at all, so genuine automatic fetching is possible here
/// without touching any anti-automation control. It lists every journal
/// issue with its publication date and PDF download links split by
/// trademark class range (e.g. "CLASS 1 - 9", "CLASS 10 - 25").
///
/// Parsing is done generically (walk every table row, read cell text,
/// read anchor hrefs) rather than against specific CSS classes/ids, since
/// I don't have the page's raw DOM to inspect directly - only the
/// text-extracted content from a fetch. This is more robust to markup
/// details I can't verify than ID-based selectors would be, but the site
/// could still change its table structure and break this; that's an
/// inherent risk of any scraper regardless of who writes it.
/// </summary>
public class JournalFetchService
{
    private const string ListingUrl = "https://search.ipindia.gov.in/IPOJournal/Journal/Trademark";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public record JournalIssueEntry(
        string JournalNumber,
        DateTime? PublicationDate,
        DateTime? AvailabilityDate,
        List<(string ClassRangeLabel, string PdfUrl)> ClassLinks);

    /// <summary>Fetches and parses the full listing. Returns issues newest-first, matching the page's own order.</summary>
    public async Task<List<JournalIssueEntry>> FetchIssuesAsync(CancellationToken ct = default)
    {
        var html = await Http.GetStringAsync(ListingUrl, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var issues = new List<JournalIssueEntry>();
        var rows = doc.DocumentNode.SelectNodes("//table//tr");
        if (rows is null) return issues;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            if (cells is null || cells.Count < 4) continue; // header row or malformed - skip

            // Expected shape: [Sr.No, Journal No, Date of Publication, Date of Availability, Download links...]
            var journalNo = cells[1].InnerText.Trim();
            if (!Regex.IsMatch(journalNo, @"^\d+")) continue; // not a data row

            var pubDate = ParseDate(cells[2].InnerText);
            var availDate = ParseDate(cells[3].InnerText);

            var classLinks = new List<(string, string)>();
            for (int i = 4; i < cells.Count; i++)
            {
                var anchors = cells[i].SelectNodes(".//a");
                if (anchors is null) continue;
                foreach (var a in anchors)
                {
                    var label = a.InnerText.Trim();
                    var href = a.GetAttributeValue("href", "");
                    if (string.IsNullOrWhiteSpace(href)) continue;
                    if (!href.StartsWith("http"))
                        href = new Uri(new Uri(ListingUrl), href).ToString();
                    classLinks.Add((label, href));
                }
            }

            issues.Add(new JournalIssueEntry(journalNo, pubDate, availDate, classLinks));
        }

        return issues;
    }

    /// <summary>
    /// Finds the issue closest to (on or before) the given date, then finds
    /// the class-range link whose numeric range contains the requested
    /// class. Returns null if no issue exists on/before that date, or none
    /// of the class-range links parse (e.g. NOTICE/WELL-KNOWN-only rows).
    /// </summary>
    public async Task<(JournalIssueEntry Issue, string ClassRangeLabel, string PdfUrl)?> FindByDateAndClassAsync(
        DateTime date, int trademarkClass, CancellationToken ct = default)
    {
        var issues = await FetchIssuesAsync(ct);

        var issue = issues
            .Where(i => i.PublicationDate is not null && i.PublicationDate <= date)
            .OrderByDescending(i => i.PublicationDate)
            .FirstOrDefault();
        if (issue is null) return null;

        foreach (var (label, url) in issue.ClassLinks)
        {
            var match = Regex.Match(label, @"CLASS\s*[_\-\s]?(\d+)\s*[-_]\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var low = int.Parse(match.Groups[1].Value);
            var high = int.Parse(match.Groups[2].Value);
            if (trademarkClass >= low && trademarkClass <= high)
                return (issue, label, url);
        }

        return null;
    }

    private static DateTime? ParseDate(string text)
    {
        text = text.Trim();
        return DateTime.TryParseExact(text, "dd/MM/yyyy", null,
            System.Globalization.DateTimeStyles.None, out var result)
            ? result
            : null;
    }
}
