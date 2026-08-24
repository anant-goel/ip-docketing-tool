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

    /// <summary>How long a single Journal PDF is allowed to take. They are large.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The listing page is small; if it has not answered in 30s it is down.</summary>
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(30);

    private static HttpClient CreateClient()
    {
        // Infinite at the client level, bounded per request. One HttpClient
        // serves both the ~100 KB listing page and multi-hundred-megabyte PDFs,
        // and a single Timeout cannot be right for both - it was 25 seconds,
        // which made large downloads impossible rather than slow.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IPDocketing/1.0 (+desktop docketing tool)");

        // application/pdf was missing. The same client fetches the PDFs, so it
        // was telling the server it accepted only HTML and then raising "the
        // server returned an HTML page rather than a PDF - the file store may be
        // down" when a content-negotiating front end obliged. The diagnostic
        // blamed the server for the header this client sent.
        client.DefaultRequestHeaders.Accept.ParseAdd("application/pdf,text/html,application/xhtml+xml,*/*;q=0.8");
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
        using var listingBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listingBudget.CancelAfter(ListingTimeout);

        var html = await Http.GetStringAsync(ListingUrl, listingBudget.Token);

        // Reset per fetch. This is a diagnostic about THIS listing read, and it
        // used to be a write-once field on a long-lived service, so the "Copy
        // raw links" button kept showing a row captured during some earlier,
        // unrelated fetch.
        LastEmptyRowHtml = null;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var issues = new List<JournalIssueEntry>();
        var rows = doc.DocumentNode.SelectNodes("//table//tr");
        if (rows is null) return issues;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            // Was: cells.Count < 4. A row whose Download cell is merged, or a
            // layout with one fewer column, was discarded whole. Three cells is
            // enough to identify an issue (number + a date); the links are found
            // by scanning the row rather than by column position.
            if (cells is null || cells.Count < 3) continue;

            // Expected shape: [Sr.No, Journal No, Date of Publication, Date of Availability, Download links...]
            // De-entitized, not just trimmed: these cells routinely arrive as
            // "&nbsp;2273&nbsp;", and Trim() does not remove a non-breaking
            // space, so the ^\d{3,5}$ test failed on every row and the whole
            // listing looked empty.
            var journalNo = CellText(cells[1]);
            // The journal number was assumed to be in cell 1. If the table ever
            // gains a leading column, every row stops matching and the listing
            // silently looks empty. Now: prefer cell 1, but fall back to the
            // first cell in the row that looks like an issue number.
            if (!Regex.IsMatch(journalNo, @"^\d{3,5}$"))
            {
                var found = false;
                for (var c = 0; c < Math.Min(cells.Count, 4); c++)
                {
                    var text = CellText(cells[c]);
                    if (Regex.IsMatch(text, @"^\d{3,5}$"))
                    {
                        journalNo = text;
                        found = true;
                        break;
                    }
                }
                if (!found) continue; // genuinely not a data row
            }

            // Scanned rather than indexed. I had just lowered the minimum cell
            // count to 3 while this still read cells[3] - an IndexOutOfRange
            // waiting for the first short row. Scanning also survives a column
            // being inserted, which indexing does not.
            DateTime? pubDate = null;
            DateTime? availDate = null;

            for (var c = 0; c < cells.Count; c++)
            {
                var parsed = ParseDate(cells[c].InnerText);
                if (parsed is null) continue;

                if (pubDate is null) pubDate = parsed;
                else if (availDate is null) { availDate = parsed; break; }
            }

            // LINK EXTRACTION - rewritten after your screenshot showed
            // "Journal 2273 has no class-range PDFs at all ... 0 link(s)".
            // The row and date parsed correctly, so the row was found; the
            // links were being dropped. Two bugs, either of which produces
            // exactly zero links:
            //
            //  1. Anchors with no href attribute were skipped outright
            //     (`if (IsNullOrWhiteSpace(href)) continue;`). ASP.NET pages
            //     routinely render navigation as <a onclick="__doPostBack(...)">
            //     or <a href="javascript:..."> with the real target in onclick.
            //     Every such link vanished silently.
            //
            //  2. The scan started at cell index 4, assuming a fixed
            //     [Sr.No | Journal | Pub | Avail | Download] layout. Any extra
            //     or missing column - or a row where the downloads sit in the
            //     same cell as something else - and the loop reads past the
            //     end, finding nothing.
            //
            // Now: scan EVERY cell, take every anchor, and accept it if a URL
            // can be recovered from href OR onclick. Cells 1-3 hold the number
            // and dates, which contain no anchors, so scanning them costs
            // nothing and removes the layout assumption entirely.
            var classLinks = new List<(string, string)>();
            var anchors = row.SelectNodes(".//a");

            if (anchors is not null)
            {
                var ordinal = 0;
                foreach (var a in anchors)
                {
                    var url = ResolveAnchorUrl(a);
                    if (url is null) continue;

                    // The Download column uses ICON links - an <img> inside an
                    // <a>, carrying no text. Requiring a text label discarded
                    // every one of them, which is exactly why rows parsed while
                    // links came back empty. A label is now derived rather than
                    // demanded.
                    classLinks.Add((DeriveLabel(a, ++ordinal, url), url));
                }
            }

            // When a row yields nothing, keep its raw HTML so the failure can
            // actually be diagnosed instead of guessed at again.
            if (classLinks.Count == 0)
                LastEmptyRowHtml ??= Truncate(row.OuterHtml, 4000);

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
        if (!IsUsableQueryDate(date)) return null;

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
        var wanted = Math.Max(1, count);

        // Dated issues newest-first, then anything whose date would not parse,
        // in the listing's own order. Sorting undated rows to DateTime.MinValue
        // dropped them off the end of the Take - and the newest issue is exactly
        // the one most likely to arrive in a date format this parser has not
        // seen, so the method whose whole job is "show me the newest issues"
        // was the most likely to hide the newest issue.
        var dated = issues
            .Where(i => i.PublicationDate is not null)
            .OrderByDescending(i => i.PublicationDate)
            .ToList();

        var undated = issues.Where(i => i.PublicationDate is null).ToList();

        return dated.Take(wanted)
            .Concat(undated.Take(Math.Max(0, wanted - dated.Count)))
            .ToList();
    }

    /// <summary>
    /// Works out a human-readable name for a link that may have no text at all.
    ///
    /// Order of preference: the anchor's own text, then title/alt/aria-label,
    /// then the alt or title of an image inside it, then the image's filename,
    /// then the PDF filename from the URL. Falling back to "Download {n}" is
    /// deliberate - a link with no name is still a link, and skipping it (the
    /// previous behaviour) loses the file entirely.
    /// </summary>
    private static string DeriveLabel(HtmlNode anchor, int ordinal, string url)
    {
        var candidates = new List<string?>
        {
            anchor.InnerText,
            anchor.GetAttributeValue("title", null),
            anchor.GetAttributeValue("alt", null),
            anchor.GetAttributeValue("aria-label", null),
        };

        var img = anchor.SelectSingleNode(".//img");
        if (img is not null)
        {
            candidates.Add(img.GetAttributeValue("alt", null));
            candidates.Add(img.GetAttributeValue("title", null));

            var src = img.GetAttributeValue("src", "") ?? "";
            if (src.Length > 0)
            {
                var name = src.Split('/', '\\').LastOrDefault()?.Split('.').FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(name)) candidates.Add(name);
            }
        }

        // A filename out of the URL is usually the most descriptive thing left.
        var fromUrl = Regex.Match(url, @"([^/\\?#]+)\.(?:pdf|zip)", RegexOptions.IgnoreCase);
        if (fromUrl.Success) candidates.Add(fromUrl.Groups[1].Value);

        var usable = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Regex.Replace(HtmlEntity.DeEntitize(c!), @"\s+", " ").Trim())
            .Where(c => c.Length is > 0 and < 120)
            .ToList();

        // AN INFORMATIVE LABEL BEATS A FIRST ONE.
        //
        // The old loop returned the first non-empty candidate, and the anchor's
        // own text is first in the list. The Download cell renders as
        // <a href=".../TMJ_2273_CLASS_26-34.pdf">PDF</a> or as a bare icon, so
        // every link in the row came back labelled "PDF" or "pdf_icon" - and the
        // URL, which names the class range explicitly, was consulted last and
        // therefore never. Class lookup then reported "none of them say which
        // classes they cover (they are icon links: PDF, PDF, PDF, PDF)" about a
        // row where every href spelled it out.
        var named = usable.FirstOrDefault(c => TryParseClassRange(c, out _, out _));
        if (named is not null) return named;

        return usable.FirstOrDefault() ?? $"Download {ordinal}";
    }

    /// <summary>
    /// Raw HTML of the first row that produced no links, captured so a parsing
    /// failure can be inspected rather than inferred. Surfaced by the
    /// "Copy raw links" diagnostic on the Journal page.
    /// </summary>
    public string? LastEmptyRowHtml { get; private set; }

    /// <summary>
    /// Recovers a URL from an anchor, whether it is in href or buried in an
    /// onclick handler.
    ///
    /// Handles the shapes an ASP.NET WebForms page actually emits:
    ///   href="/IPOJournal/.../file.pdf"        - plain relative link
    ///   href="javascript:void(0)" onclick="window.open('...')"
    ///   onclick="__doPostBack('ctl00$...','')" - postback, no URL at all
    ///
    /// The last case genuinely has no fetchable URL - the file only exists
    /// after a form submission - and returns null rather than a fake link that
    /// would 404 later and look like a download failure.
    /// </summary>
    private static string? ResolveAnchorUrl(HtmlNode anchor)
    {
        var href = anchor.GetAttributeValue("href", "") ?? "";
        href = HtmlEntity.DeEntitize(href).Trim();

        if (href.Length > 0 &&
            !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
            !href.StartsWith("#"))
        {
            return Absolutize(href);
        }

        // Fall back to a URL embedded in onclick / href="javascript:...".
        var script = anchor.GetAttributeValue("onclick", "") ?? "";
        var combined = HtmlEntity.DeEntitize(script + " " + href);

        // A quoted path, typically inside window.open('...') or location.href='...'
        var quoted = Regex.Match(combined, @"['""]([^'""]*\.(?:pdf|zip)(?:\?[^'""]*)?)['""]",
            RegexOptions.IgnoreCase);
        if (quoted.Success) return Absolutize(quoted.Groups[1].Value);

        var anyQuotedPath = Regex.Match(combined, @"['""](/[^'""\s]+|https?://[^'""\s]+)['""]");
        if (anyQuotedPath.Success) return Absolutize(anyQuotedPath.Groups[1].Value);

        return null;
    }

    /// <summary>
    /// Readable text of one table cell: HTML entities decoded, non-breaking
    /// spaces turned into ordinary ones, runs of whitespace collapsed.
    /// </summary>
    private static string CellText(HtmlNode cell) =>
        Regex.Replace(
            HtmlEntity.DeEntitize(cell.InnerText ?? string.Empty).Replace('\u00A0', ' '),
            @"\s+", " ").Trim();

    private static string Absolutize(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(new Uri(ListingUrl), url).ToString();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + " ...(truncated)";

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
    /// variants into one case.
    ///
    /// A SINGLE CLASS IS ALSO A RANGE. This used to require two numbers and a
    /// hyphen, and returned false for "CLASS 35" and "CLASS_35_PART_1" - the
    /// comment here even called that "correct". It is not correct for lookup.
    /// Class 35 is the highest-volume class on the register and is routinely
    /// published as its own split files, so a user asking for class 35 was told
    /// "Class 35 isn't covered by Journal 2273 - its ranges are: CLASS 1-9,
    /// CLASS 10-25, CLASS 26-34, CLASS 36-45", a definitive-sounding statement
    /// that it had not been published, while two files containing it sat
    /// unlisted in the same row. A lone class now parses as the range n..n.
    ///
    /// Labels with no class number at all - "NOTICE", "WELL KNOWN TRADE MARKS",
    /// "PDF" - still return false; those are real links, just not class ones.
    /// </summary>
    /// <summary>
    /// Rejects a date that cannot have come from a person.
    ///
    /// ParseDate guards SCRAPED dates with a year floor, but the date PARAMETER
    /// was never validated - and an unset WinUI date picker hands back the WinRT
    /// epoch, 01 Jan 1601. That sentinel was accepted as a legitimate query and
    /// produced "No issue was published on or before 01 Jan 1601", a message
    /// that describes the listing rather than the actual problem, which is that
    /// no date was chosen.
    /// </summary>
    public static bool IsUsableQueryDate(DateTime date) => date.Year >= 1900;

    public static bool TryParseClassRange(string label, out int low, out int high)
    {
        low = 0;
        high = 0;
        if (string.IsNullOrWhiteSpace(label)) return false;

        // Underscores and dots to spaces (filenames use both); every dash
        // variant the listing has ever used folded to one; "TO" as a separator.
        var normalized = Regex.Replace(label.Replace('_', ' ').Replace('.', ' '),
            @"[\u2010-\u2015]", "-");
        normalized = Regex.Replace(normalized, @"\bTO\b", "-", RegexOptions.IgnoreCase);

        // CLASS or CLASSES, then n, optionally - m.
        var match = Regex.Match(normalized,
            @"CLASS(?:ES)?\s*[:\-]?\s*(\d{1,2})\s*(?:-\s*(\d{1,2}))?",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        low = int.Parse(match.Groups[1].Value);
        high = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : low;

        // Nice classes run 1-45. Anything outside that came from a year, a
        // volume number or an issue number that happened to follow the word.
        if (low is < 1 or > 45 || high is < 1 or > 45) return false;

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

        if (!IsUsableQueryDate(date))
            return ClassLookupResult.Failure(
                $"No publication date has been chosen - the date box is reading " +
                $"{date:dd MMM yyyy}, which is the value an unset date picker returns. " +
                "Pick a date and try again.");

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

        if (ranges.Count > 0)
            return ClassLookupResult.Failure(
                $"Class {trademarkClass} isn't covered by Journal {issue.JournalNumber} " +
                $"({issue.PublicationDate:dd MMM yyyy}). Its ranges are: {string.Join(", ", ranges)}.");

        // No label parses as a class range. That is expected when the Download
        // column uses icon links, which carry no text describing what they
        // contain - so which file holds which class simply cannot be known from
        // the listing. Saying so beats implying the issue has no PDFs, and
        // beats guessing at a file that might be the wrong classes entirely.
        if (issue.ClassLinks.Count > 0)
            return ClassLookupResult.Failure(
                $"Journal {issue.JournalNumber} ({issue.PublicationDate:dd MMM yyyy}) has " +
                $"{issue.ClassLinks.Count} download link(s), but none of them say which classes they cover " +
                $"(they are icon links: {string.Join(", ", issue.ClassLinks.Take(4).Select(l => l.ClassRangeLabel))}). " +
                "Class-based lookup can't work on this issue - use \"Get journal PDFs\" to fetch them all, " +
                "then search inside them by name.");

        return ClassLookupResult.Failure(
            $"Journal {issue.JournalNumber} ({issue.PublicationDate:dd MMM yyyy}) produced no download links at all. " +
            "Use Journal tools > Self-test to capture the raw row HTML.");
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

        // THE CACHE KEY MUST INCLUDE WHICH FILE THIS IS.
        //
        // It used to be journal_{issue}.pdf - derived from the issue number
        // alone. But one journal issue is five or six separate class-range PDFs,
        // and every one of them mapped to that same path. So: fetch class 5 for
        // issue 2273 and the CLASS 1-9 file lands at journal_2273.pdf. Then ask
        // for class 29. The lookup correctly resolves the CLASS 26-34 URL, this
        // method sees a large journal_2273.pdf already on disk, and returns it
        // WITHOUT ISSUING A REQUEST. The caller searches the class 1-9 file,
        // finds no class 29 marks, and reports the mark as unpublished. The URL
        // was never part of the key and was never compared against what was on
        // disk, so re-running could not fix it and nothing ever expired.
        //
        // That is the "I asked for class 29 and got 1-9" report, and it survives
        // at this layer even once the lookup above is correct.
        var finalPath = Path.Combine(libraryPath, $"journal_{safeIssue}_{FileTagFor(url)}.pdf");
        var tempPath = finalPath + ".part";

        // BUG FIX: this used to return any existing file at this path, however
        // small. A previous run that saved a 4 KB "service unavailable" page
        // under a .pdf name therefore poisoned the cache permanently - every
        // later download short-circuited to the error page, and the search that
        // read it reported "not found" on a name that was in the Journal. A
        // file only counts as already-downloaded if it is plausibly a Journal
        // AND actually begins with a PDF header.
        if (File.Exists(finalPath))
        {
            if (new FileInfo(finalPath).Length >= 20_000 && LooksLikePdf(finalPath)) return finalPath;
            try { File.Delete(finalPath); } catch { /* re-download will overwrite */ }
        }

        // A .part left behind by an interrupted run must not be appended to or
        // mistaken for progress.
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

        // A journal PDF runs to hundreds of megabytes, and the shared client's
        // 25-second timeout is sized for the ~100 KB listing page. HttpClient's
        // Timeout covers the WHOLE operation, and with ResponseHeadersRead the
        // clock keeps running through the body copy - so a 180 MB file on
        // anything slower than about 60 Mbit could never finish, and surfaced as
        // "could not be read" rather than "still downloading". The download gets
        // its own, generous budget.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(DownloadTimeout);

        Exception? lastFailure = null;

        // Three attempts. A dropped connection partway through a large file is
        // ordinary on this host, and there was previously no retry anywhere in
        // the service - one blip meant the whole search reported the issue as
        // unreadable.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, tempPath, budget.Token);

                File.Move(tempPath, finalPath, overwrite: true);
                return finalPath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), budget.Token);
            }
        }

        throw new InvalidOperationException(
            $"The Journal PDF could not be downloaded after 3 attempts: {lastFailure?.Message}", lastFailure);
    }

    /// <summary>One download attempt, validated before it is allowed to count.</summary>
    private static async Task DownloadOnceAsync(string url, string tempPath, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Content-type is checked because IP India serves an HTML maintenance
        // page with a 200 status when the file store is down - saving that as a
        // .pdf produces a file that fails much later and far less obviously.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The server returned an HTML page rather than a PDF - the Journal file store may be down.");

        var declaredLength = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(tempPath))
        {
            await source.CopyToAsync(target, ct);
        }

        // NOTHING used to be checked after the copy. A connection dropped at
        // 40 MB of a 150 MB file returned from CopyToAsync perfectly normally,
        // the .part was renamed to the final name, and the truncated file then
        // satisfied the 20 KB cache test on every subsequent run - so the search
        // reported "searched in full" while 110 MB of the issue had never been
        // read. Three tests now have to pass before a file counts.
        var written = new FileInfo(tempPath).Length;

        if (declaredLength is { } expected && written != expected)
            throw new InvalidOperationException(
                $"The download was cut short - {written:N0} bytes arrived of {expected:N0} expected.");

        if (written < 20_000)
            throw new InvalidOperationException(
                $"The downloaded file is only {written:N0} bytes, which is too small to be a Journal PDF. " +
                "The server most likely returned an error page.");

        if (!LooksLikePdf(tempPath))
            throw new InvalidOperationException(
                "The downloaded file is not a PDF - it does not begin with a PDF header. " +
                "The server most likely returned an error page with a misleading content type.");
    }

    /// <summary>
    /// True when the file actually begins with "%PDF-". The size floor alone was
    /// standing in for this, and a 30 KB ASP.NET error page served as
    /// application/octet-stream sailed past it.
    /// </summary>
    private static bool LooksLikePdf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            return stream.Read(header) == 5 &&
                   header[0] == (byte)'%' && header[1] == (byte)'P' &&
                   header[2] == (byte)'D' && header[3] == (byte)'F' && header[4] == (byte)'-';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A short, stable, readable tag identifying WHICH file of an issue this is.
    /// Prefers the class range named in the URL, so the library reads
    /// journal_2273_class_26-34.pdf; falls back to a hash of the URL when the
    /// filename says nothing, which still guarantees two different links never
    /// collide on one path.
    /// </summary>
    private static string FileTagFor(string url)
    {
        var name = Regex.Match(url, @"([^/\\?#]+)\.(?:pdf|zip)", RegexOptions.IgnoreCase);

        if (name.Success && TryParseClassRange(name.Groups[1].Value, out var low, out var high))
            return low == high ? $"class_{low}" : $"class_{low}-{high}";

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));

        return "u" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Reads a publication date out of a table cell.
    ///
    /// BUG FIX: this used to accept exactly one format, "dd/MM/yyyy", against
    /// the cell's raw InnerText. Two things broke it in practice:
    ///
    ///   1. The cell text arrives HTML-encoded and padded - "&amp;nbsp;12/08/2026 "
    ///      is not "12/08/2026", and TryParseExact rejects it outright.
    ///   2. The listing does not use one format. Recent rows read "12/08/2026",
    ///      older ones "12-08-2026", and some render "12 Aug 2026".
    ///
    /// Every row whose date failed to parse got PublicationDate = null, which
    /// then made FindByDateAndClassAsync report "no issue was published on or
    /// before {date}" no matter what date was asked for - the class lookup was
    /// never the problem. The text is now de-entitized and whitespace-collapsed
    /// first, and several day-first formats are tried.
    ///
    /// Day-first is asserted explicitly with InvariantCulture rather than left
    /// to the machine's locale: on a US-locale machine, "05/08/2026" silently
    /// parsed as 8 May instead of 5 August, which is a whole quarter's drift in
    /// a docketing tool.
    /// </summary>
    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = Regex.Replace(
            HtmlEntity.DeEntitize(text).Replace('\u00A0', ' '), @"\s+", " ").Trim();

        if (cleaned.Length == 0) return null;

        // A cell like "Date of Publication : 12/08/2026" - take the date part.
        var embedded = Regex.Match(cleaned,
            @"\b(\d{1,2}[\/\-\.\s][A-Za-z0-9]{1,9}[\/\-\.\s]\d{2,4})\b");
        if (embedded.Success) cleaned = embedded.Groups[1].Value.Trim();
        else
        {
            // ...and the month-first shape, which the pattern above cannot see.
            var monthFirst = Regex.Match(cleaned,
                @"\b([A-Za-z]{3,9}\s+\d{1,2},?\s+\d{4})\b");
            if (monthFirst.Success) cleaned = monthFirst.Groups[1].Value.Trim();
        }

        string[] formats =
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yy", "d/M/yy",
            "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "d-MMM-yyyy",
            "dd MMMM yyyy", "d MMMM yyyy", "yyyy-MM-dd",
            // Month-first spellings. These matter more than they look: the row
            // scan below takes the FIRST cell in the row that parses, so if the
            // publication cell renders as "Aug 17, 2026" and fails while the
            // availability cell reads "24/08/2026" and succeeds, the issue is
            // recorded as published on the 24th. A lookup for the 17th then
            // skips it and silently returns the PREVIOUS week's journal.
            "MMM d, yyyy", "MMM dd, yyyy", "MMMM d, yyyy", "MMMM dd, yyyy",
            "MMM d yyyy", "MMMM d yyyy",
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(cleaned, format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var result))
            {
                // A two-digit year parsed as 1926 is a typo, not a back issue.
                return result.Year < 1900 ? null : result.Date;
            }
        }

        return null;
    }
}
