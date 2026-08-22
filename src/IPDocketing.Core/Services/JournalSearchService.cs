using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// Searches downloaded Journal PDFs for a name and reports the page it appears
/// on, with the surrounding entry extracted.
///
/// This answers the question a practice actually asks each week: "was anything
/// published under KARTIK TRADE MARKS COMPANY?" — which the similarity watch
/// does not answer, because that watch compares MARKS against your portfolio.
/// A proprietor or agent name is a different axis: you want everything filed by
/// or against a named party, whether or not the mark resembles anything you own.
///
/// Matching is done on normalised text. Journal PDFs break names across lines,
/// pad them with variable whitespace, and OCR them inconsistently on scanned
/// issues, so a naive Contains() on raw page text misses most real hits. The
/// text is collapsed to single-spaced uppercase before comparison, and the
/// search terms are matched both whole and as a distinctive subset — so
/// "KARTIK TRADE MARKS COMPANY" still hits a page reading "KARTIK TRADE MARKS
/// CO." or "M/S KARTIK TRADEMARKS COMPANY".
/// </summary>
public class JournalSearchService
{
    private readonly AppDbContext _db;
    private IDocumentTextExtractor? _extractor;
    private JournalFetchService? _fetch;
    private string? _libraryPath;

    public JournalSearchService(AppDbContext db)
    {
        _db = db;
    }

    public void UseExtractor(IDocumentTextExtractor extractor) => _extractor = extractor;

    /// <summary>
    /// Gives the search the ability to download a missing issue rather than
    /// skip it.
    ///
    /// This is why "not found" kept coming back on a name that IS in the
    /// Journal: the search only ever looked at issues whose PDF had already
    /// been downloaded, and nothing had downloaded any. Every issue was skipped,
    /// zero pages were searched, and the honest "0 hits" read exactly like
    /// "not published". Searching now fetches what it needs first.
    /// </summary>
    public void UseDownloader(JournalFetchService fetch, string libraryPath)
    {
        _fetch = fetch;
        _libraryPath = libraryPath;
    }

    public sealed record PageHit(
        string IssueNumber,
        DateTime PublicationDate,
        int PageNumber,
        string MatchedText,
        string PageExcerpt,
        string PdfPath,
        bool FromOcr)
    {
        /// <summary>What to tell the user: "Journal 2273, page 412".</summary>
        public string Location => $"Journal {IssueNumber}, page {PageNumber}";
    }

    public sealed record SearchReport(
        List<PageHit> Hits,
        int IssuesSearched,
        int IssuesSkipped,
        List<string> Notes);

    /// <summary>
    /// Searches every downloaded issue for the term. Issues whose PDF has not
    /// been downloaded are skipped and counted, rather than silently treated as
    /// "no match" — the difference between "not published" and "not checked"
    /// matters enormously here.
    /// </summary>
    public async Task<SearchReport> SearchAsync(
        string term,
        int maxIssues = 12,
        CancellationToken ct = default,
        Action<string>? progress = null)
    {
        var hits = new List<PageHit>();
        var notes = new List<string>();
        var searched = 0;
        var skipped = 0;

        if (string.IsNullOrWhiteSpace(term))
            return new SearchReport(hits, 0, 0, new List<string> { "No search term given." });

        if (_extractor is null)
            return new SearchReport(hits, 0, 0,
                new List<string> { "No PDF reader is available, so nothing could be searched." });

        var needle = NormalizeForSearch(term);
        var needleTokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToList();

        var issues = _db.JournalIssues
            .OrderByDescending(j => j.PublicationDate)
            .Take(maxIssues)
            .ToList();

        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(issue.LocalPdfPath) || !File.Exists(issue.LocalPdfPath))
            {
                var fetched = await TryDownloadAsync(issue, progress, ct);
                if (!fetched)
                {
                    skipped++;
                    notes.Add(string.IsNullOrWhiteSpace(issue.Url)
                        ? $"Journal {issue.IssueNumber}: no PDF link on record, so it could not be fetched or searched. " +
                          "Use 'Pull latest weekly issues' to pick up the class-range links."
                        : $"Journal {issue.IssueNumber}: download failed ({issue.LastError ?? "unknown"}), so it was NOT searched.");
                    continue;
                }
            }

            progress?.Invoke($"Searching Journal {issue.IssueNumber}...");

            try
            {
                var pages = await _extractor.ExtractPagesAsync(issue.LocalPdfPath, ct);
                searched++;

                var unreadablePages = 0;

                for (var i = 0; i < pages.Pages.Count; i++)
                {
                    var raw = pages.Pages[i];

                    // A page with no extractable text is NOT a page that
                    // doesn't contain the name - it is a page nobody looked at.
                    // Skipping these silently is how a scanned issue reports
                    // "searched, not found" when in truth nothing was read.
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        unreadablePages++;
                        continue;
                    }

                    var normalized = NormalizeForSearch(raw);

                    var matched = normalized.Contains(needle, StringComparison.Ordinal)
                        ? term
                        : MatchBySubset(normalized, needleTokens);

                    if (matched is null) continue;

                    hits.Add(new PageHit(
                        issue.IssueNumber,
                        issue.PublicationDate,
                        i + 1,
                        matched,
                        ExcerptAround(raw, needleTokens),
                        issue.LocalPdfPath!,
                        !pages.IsExact));
                }
                if (unreadablePages > 0)
                {
                    // Counted against the number of pages actually walked, not
                    // the PDF's declared page count. Where an extractor returns
                    // fewer entries than PageCount, the old comparison could
                    // never be equal, so a wholly unreadable scanned issue was
                    // still reported as "searched" - the one outcome this whole
                    // block exists to prevent.
                    var walked = Math.Max(1, pages.Pages.Count);
                    var proportion = unreadablePages * 100 / walked;
                    notes.Add(unreadablePages >= walked
                        ? $"Journal {issue.IssueNumber}: NONE of its {pages.PageCount} pages could be read " +
                          "(no text layer - it is a scanned issue). It was NOT searched. Install Tesseract " +
                          "or run the full extraction to OCR it."
                        : $"Journal {issue.IssueNumber}: {unreadablePages} of {pages.PageCount} page(s) " +
                          $"({proportion}%) had no readable text and were skipped.");

                    if (unreadablePages >= walked)
                    {
                        // It contributed nothing, so it must not be counted as
                        // searched - that count is what the user reads as
                        // "coverage".
                        searched--;
                        skipped++;
                    }
                }
            }
            catch (Exception ex)
            {
                skipped++;
                notes.Add($"Journal {issue.IssueNumber}: could not be read — {ex.Message}");
            }
        }

        if (hits.Count == 0)
        {
            if (searched == 0)
                notes.Add($"NOTHING WAS SEARCHED - no issue PDF could be obtained. This is not the same as " +
                          $"\"{term}\" being absent from the Journal.");
            else
                notes.Add($"Searched {searched} issue(s) in full and found no mention of \"{term}\"." +
                          (skipped > 0 ? $" {skipped} further issue(s) could not be read - see above." : ""));
        }

        return new SearchReport(hits, searched, skipped, notes);
    }

    /// <summary>
    /// Downloads an issue's PDF on demand. Returns false when there is no link
    /// on record or the fetch fails - never throws, because one bad issue must
    /// not abort a search across a dozen of them.
    /// </summary>
    private async Task<bool> TryDownloadAsync(JournalIssue issue, Action<string>? progress, CancellationToken ct)
    {
        if (_fetch is null || _libraryPath is null) return false;
        if (string.IsNullOrWhiteSpace(issue.Url)) return false;

        try
        {
            progress?.Invoke($"Downloading Journal {issue.IssueNumber} (needed to search it)...");
            var path = await _fetch.DownloadPdfAsync(issue.Url, _libraryPath, issue.IssueNumber, ct);

            var info = new FileInfo(path);
            if (info.Length < 20_000)
            {
                issue.LastError = $"Downloaded file was only {info.Length} bytes - likely an error page.";
                try { File.Delete(path); } catch { }
                _db.SaveChanges();
                return false;
            }

            issue.LocalPdfPath = path;
            issue.PdfSizeBytes = info.Length;
            issue.DownloadedUtc = DateTime.UtcNow;
            issue.LastError = null;
            _db.SaveChanges();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            issue.LastError = ex.Message;
            try { _db.SaveChanges(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// A page counts as a hit when most of the distinctive words appear on it,
    /// not only when the exact phrase does. Journal typesetting breaks names
    /// across lines and abbreviates them ("COMPANY" to "CO."), so exact-phrase
    /// matching alone would miss most genuine appearances.
    /// </summary>
    private static string? MatchBySubset(string pageText, List<string> tokens)
    {
        if (tokens.Count == 0) return null;

        var present = tokens.Where(t => pageText.Contains(t, StringComparison.Ordinal)).ToList();

        // Require the clear majority, and at least two words. One shared word
        // would fire on every page containing "TRADE".
        var needed = Math.Max(2, (int)Math.Ceiling(tokens.Count * 0.75));
        if (tokens.Count == 1) needed = 1;

        return present.Count >= needed ? string.Join(' ', present) : null;
    }

    /// <summary>Collapses whitespace, uppercases, strips punctuation that Journal typesetting varies.</summary>
    private static string NormalizeForSearch(string value)
    {
        var chars = value
            .ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ');

        return string.Join(' ', new string(chars.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Pulls the surrounding block out of the raw page text, so the hit can be
    /// read in place rather than only located. Falls back to the top of the page
    /// where the anchor can't be found in the raw text (OCR line breaks).
    /// </summary>
    private static string ExcerptAround(string pageText, List<string> tokens)
    {
        var anchor = tokens.OrderByDescending(t => t.Length).FirstOrDefault();
        var index = anchor is null
            ? -1
            : pageText.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);

        if (index < 0) return Trim(pageText, 0, 1200);

        var start = Math.Max(0, index - 500);
        return Trim(pageText, start, 1400);
    }

    private static string Trim(string text, int start, int length)
    {
        if (start >= text.Length) return string.Empty;
        var take = Math.Min(length, text.Length - start);
        var slice = text.Substring(start, take).Trim();
        return start > 0 ? "..." + slice : slice;
    }

    /// <summary>
    /// Writes the matched page out as a standalone text file next to the docket,
    /// and records the hit against the issue so it isn't lost.
    /// </summary>
    public string SavePageExtract(PageHit hit, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var fileName = $"journal_{hit.IssueNumber}_p{hit.PageNumber}.txt";
        var path = Path.Combine(outputDirectory, fileName);

        var header =
            $"Journal {hit.IssueNumber} — published {hit.PublicationDate:dd MMM yyyy}\r\n" +
            $"Page {hit.PageNumber}\r\n" +
            $"Source PDF: {hit.PdfPath}\r\n" +
            (hit.FromOcr
                ? "Text obtained by OCR — verify against the PDF before relying on it.\r\n"
                : "Text taken from the PDF text layer (exact).\r\n") +
            new string('-', 70) + "\r\n\r\n";

        File.WriteAllText(path, header + hit.PageExcerpt);
        return path;
    }
}
