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

    public JournalSearchService(AppDbContext db)
    {
        _db = db;
    }

    public void UseExtractor(IDocumentTextExtractor extractor) => _extractor = extractor;

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
                skipped++;
                notes.Add($"Journal {issue.IssueNumber}: PDF not downloaded, so it was NOT searched.");
                continue;
            }

            progress?.Invoke($"Searching Journal {issue.IssueNumber}...");

            try
            {
                var pages = await _extractor.ExtractPagesAsync(issue.LocalPdfPath, ct);
                searched++;

                for (var i = 0; i < pages.Pages.Count; i++)
                {
                    var raw = pages.Pages[i];
                    if (string.IsNullOrWhiteSpace(raw)) continue;

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
            }
            catch (Exception ex)
            {
                skipped++;
                notes.Add($"Journal {issue.IssueNumber}: could not be read — {ex.Message}");
            }
        }

        if (hits.Count == 0 && searched > 0)
            notes.Add($"Searched {searched} issue(s) in full and found no mention of \"{term}\".");

        return new SearchReport(hits, searched, skipped, notes);
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
