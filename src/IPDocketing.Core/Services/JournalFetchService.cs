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
    // A default HttpClient sends no User-Agent at all, which some government
    // front-ends reject outright or serve a different page to. Identifying the
    // app honestly is both more reliable and the correct thing to do - this is
    // a public, login-free page being read as a normal client, not an attempt
    // to look like something it isn't.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IPDocketing/1.0 (+desktop docketing tool)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        return client;
    }

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
            if (!TryParseClassRange(label, out var low, out var high)) continue;
            if (trademarkClass >= low && trademarkClass <= high)
                return (issue, label, url);
        }

        return null;
    }

    /// <summary>
    /// The most recent issues, newest first - what the docx section 4 page wants
    /// ("links of the Trade Marks Journal published weekly every Monday"),
    /// rather than having to know a class number first.
    /// </summary>
    public async Task<List<JournalIssueEntry>> GetLatestIssuesAsync(int count = 8, CancellationToken ct = default)
    {
        var issues = await FetchIssuesAsync(ct);
        return issues
            .OrderByDescending(i => i.PublicationDate ?? DateTime.MinValue)
            .Take(Math.Max(1, count))
            .ToList();
    }

    /// <summary>
    /// Parses a class-range link label into its low and high class numbers.
    ///
    /// Verified against the live listing (checked 21 Aug 2026). Current issues
    /// label their links "CLASS 26 - 34"; issues from roughly 2012 and earlier
    /// use "CLASS_26_-_34" with underscores as separators, and a handful use
    /// "CLASS_1-4" with no spaces at all. The previous pattern handled the
    /// modern form but not the underscore form, so back-issue lookups silently
    /// found nothing.
    ///
    /// Underscores are normalised to spaces first, which collapses all three
    /// variants into one case. Labels with no range at all - "CLASS_35_PART_1",
    /// "NOTICE", "WELL KNOWN TRADE MARKS" - correctly return false; those are
    /// real links, just not class-range ones.
    /// </summary>
    public static bool TryParseClassRange(string label, out int low, out int high)
    {
        low = 0;
        high = 0;
        if (string.IsNullOrWhiteSpace(label)) return false;

        var normalized = label.Replace('_', ' ');
        var match = Regex.Match(normalized, @"CLASS[\s]*(\d+)[\s]*[-\u2013][\s]*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        low = int.Parse(match.Groups[1].Value);
        high = int.Parse(match.Groups[2].Value);
        return low <= high;
    }

    /// <summary>
    /// Same lookup as FindByDateAndClassAsync, but explains exactly which step
    /// failed instead of returning a bare null.
    ///
    /// The old caller printed "No journal issue found on/before {date}, or
    /// class {n} wasn't in a parseable range" - one message covering two
    /// completely different failures, so it always named both and identified
    /// neither. The real cause was invariably the first (an unset date picker
    /// returning 01-Jan-1601); the class range was fine, but the message
    /// implicated it every time.
    /// </summary>
    public async Task<ClassLookupResult> FindByDateAndClassDetailedAsync(
        DateTime date, int trademarkClass, CancellationToken ct = default)
    {
        List<JournalIssueEntry> issues;
        try
        {
            issues = await FetchIssuesAsync(ct);
        }
        catch (Exception ex)
        {
            return ClassLookupResult.Failure(
                $"Could not read the Journal listing page: {ex.Message}");
        }

        if (issues.Count == 0)
            return ClassLookupResult.Failure(
                "The listing page loaded but no issue rows could be parsed from it. " +
                "Its table layout may have changed.");

        var dated = issues.Where(i => i.PublicationDate is not null).ToList();
        var issue = dated
            .Where(i => i.PublicationDate <= date)
            .OrderByDescending(i => i.PublicationDate)
            .FirstOrDefault();

        if (issue is null)
        {
            var earliest = dated.Min(i => i.PublicationDate);
            return ClassLookupResult.Failure(
                $"No issue was published on or before {date:dd MMM yyyy}. " +
                $"The listing covers {earliest:dd MMM yyyy} to {dated.Max(i => i.PublicationDate):dd MMM yyyy} " +
                $"({issues.Count} issues). Check the date.");
        }

        foreach (var (label, url) in issue.ClassLinks)
        {
            if (!TryParseClassRange(label, out var low, out var high)) continue;
            if (trademarkClass >= low && trademarkClass <= high)
                return ClassLookupResult.Success(issue, label, url);
        }

        var ranges = issue.ClassLinks
            .Where(l => TryParseClassRange(l.ClassRangeLabel, out _, out _))
            .Select(l => l.ClassRangeLabel)
            .ToList();

        return ClassLookupResult.Failure(ranges.Count == 0
            ? $"Journal {issue.JournalNumber} ({issue.PublicationDate:dd MMM yyyy}) has no class-range PDFs at all - " +
              $"it only carries {issue.ClassLinks.Count} notice/well-known-marks link(s)."
            : $"Class {trademarkClass} isn't covered by Journal {issue.JournalNumber} " +
              $"({issue.PublicationDate:dd MMM yyyy}). Its ranges are: {string.Join(", ", ranges)}.");
    }

    public sealed record ClassLookupResult(
        bool Found,
        JournalIssueEntry? Issue,
        string? ClassRangeLabel,
        string? PdfUrl,
        string? Reason)
    {
        public static ClassLookupResult Success(JournalIssueEntry issue, string label, string url) =>
            new(true, issue, label, url, null);

        public static ClassLookupResult Failure(string reason) =>
            new(false, null, null, null, reason);
    }

    /// <summary>
    /// Downloads a Journal PDF into the local library. Streams to a temporary
    /// file and renames on success, so an interrupted download can never leave
    /// a half-written file that later looks like a complete one.
    /// </summary>
    public async Task<string> DownloadPdfAsync(string url, string libraryPath, string issueNumber,
                                               CancellationToken ct = default)
    {
        Directory.CreateDirectory(libraryPath);

        var safeIssue = string.Concat(issueNumber.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var finalPath = Path.Combine(libraryPath, $"journal_{safeIssue}.pdf");
        var tempPath = finalPath + ".part";

        if (File.Exists(finalPath)) return finalPath;

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Content-type is checked because IP India serves an HTML maintenance
        // page with a 200 status when the file store is down - saving that as a
        // .pdf produces a file that fails much later and far less obviously.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The server returned an HTML page rather than a PDF - the Journal file store may be down.");

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(tempPath))
        {
            await source.CopyToAsync(target, ct);
        }

        File.Move(tempPath, finalPath, overwrite: true);
        return finalPath;
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
