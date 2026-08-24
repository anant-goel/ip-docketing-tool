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
        bool FromOcr,
        int Strength = 0)
    {
        /// <summary>What to tell the user: "Journal 2273, page 412".</summary>
        public string Location => $"Journal {IssueNumber}, page {PageNumber}";

        /// <summary>
        /// How the hit was found, in words. A reviewer deciding which of forty
        /// pages to open first needs to know that one is the exact name and the
        /// rest share three words with it.
        /// </summary>
        public string MatchQuality => Strength switch
        {
            >= 100 => "exact name",
            >= 95 => "exact name, broken across lines",
            >= 85 => "every word present",
            >= 70 => "most of the name present",
            > 0 => "partial match",
            _ => "match",
        };
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

        // The search plan is built per issue, inside the loop - the word weights
        // depend on which pages are being searched, so they cannot be computed
        // here.

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
                // Read from the cache where the PDF has not changed since it was
                // last parsed. Extracting a 1,500-page journal takes tens of
                // seconds with a text layer and can take hours through OCR, and
                // it was being redone from scratch on EVERY search - so looking
                // up three different proprietor names meant three full
                // re-extractions of every downloaded issue.
                var pages = await ExtractCachedAsync(issue.LocalPdfPath!, progress, ct);
                searched++;

                var unreadablePages = 0;

                // Normalise once per page, then weight the search words against
                // THIS issue before matching anything. Both halves matter: the
                // page text was previously normalised inside the loop and thrown
                // away, and the weights cannot be known until the issue has been
                // read.
                var normalizedPages = pages.Pages
                    .Select(p => string.IsNullOrWhiteSpace(p) ? string.Empty : NormalizeForSearch(p))
                    .ToList();

                var plan = BuildPlan(term, normalizedPages.Where(p => p.Length > 0).ToList());

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

                    var match = MatchPage(normalizedPages[i], plan);
                    if (match is null) continue;

                    hits.Add(new PageHit(
                        issue.IssueNumber,
                        issue.PublicationDate,
                        i + 1,
                        match.MatchedText,
                        ExcerptAround(raw, match.Anchor, plan),
                        issue.LocalPdfPath!,
                        !pages.IsExact,
                        match.Strength));
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
                // NOT "in full". One JournalIssue row holds one PDF, and an
                // issue is published as several class-range files - so what was
                // searched is the class range on record for each issue, not the
                // whole issue. Saying "in full" invites a clearance conclusion
                // the search cannot support.
                notes.Add($"Searched the downloaded PDF of {searched} issue(s) and found no mention of \"{term}\". " +
                          "Note that each issue is published as several class-range files and only the file " +
                          "on record for each issue was read - a mark published in another class range of the " +
                          "same issue would not appear here." +
                          (skipped > 0 ? $" {skipped} further issue(s) could not be read - see above." : ""));
        }

        // STRONGEST FIRST. Hits used to come back in the order the pages were
        // walked, so an exact-name match on page 900 sat below forty
        // partial matches from page 12 onwards. A reviewer works down this list
        // and stops when they find what they came for; the order is most of the
        // value.
        var ranked = hits
            .OrderByDescending(h => h.Strength)
            .ThenByDescending(h => h.PublicationDate)
            .ThenBy(h => h.PageNumber)
            .ToList();

        return new SearchReport(ranked, searched, skipped, notes);
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
    /// Extracts a PDF's pages, reusing a cached copy where the file has not
    /// changed since the last extraction.
    ///
    /// The cache is a sidecar file next to the PDF whose first line records the
    /// source file's size and last-write time. It is used only when BOTH still
    /// match, so a re-downloaded or repaired PDF is re-read rather than answered
    /// from a stale cache - which is the failure mode that would make a cache
    /// worse than no cache.
    ///
    /// On disk rather than in the database, deliberately: the page text of a
    /// dozen issues runs to hundreds of megabytes and does not belong in a
    /// SQLite file that gets backed up. It also means no schema change, so
    /// nothing here puts the existing database at risk.
    /// </summary>
    private async Task<PagedExtractionResult> ExtractCachedAsync(
        string pdfPath, Action<string>? progress, CancellationToken ct)
    {
        var cachePath = pdfPath + ".pages.txt";

        var info = new FileInfo(pdfPath);
        var stamp = $"IPD-PAGECACHE v1 {info.Length} {info.LastWriteTimeUtc.Ticks}";

        if (File.Exists(cachePath))
        {
            try
            {
                var cached = await File.ReadAllTextAsync(cachePath, ct);
                var split = cached.IndexOf('\n');

                if (split > 0 && cached[..split].TrimEnd('\r') == stamp)
                {
                    var body = cached[(split + 1)..];

                    // A form feed separates the pages: it is the one character
                    // that will not occur in extracted page text, so a page full
                    // of punctuation still round-trips intact.
                    var parts = body.Split('\f');
                    var method = parts.Length > 0 ? parts[0] : ExtractionResult.TextLayer;

                    return new PagedExtractionResult(parts.Skip(1).ToList(), method);
                }
            }
            catch
            {
                // An unreadable cache is not an error - it just means the PDF is
                // read the slow way, which is what happened every time before.
            }
        }

        progress?.Invoke($"Reading {Path.GetFileName(pdfPath)} (first time - this is the slow part)...");

        var pages = await _extractor!.ExtractPagesAsync(pdfPath, ct);

        // Never cache a failed or empty read. That would turn one bad extraction
        // into a permanent "this issue contains nothing".
        if (pages.Error is null && pages.Pages.Any(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                await File.WriteAllTextAsync(
                    cachePath,
                    stamp + "\n" + pages.Method + "\f" + string.Join('\f', pages.Pages),
                    ct);
            }
            catch
            {
                // A read-only library folder must not fail the search.
            }
        }

        return pages;
    }

    /// <summary>
    /// How strong a page match is, and what matched.
    ///
    /// <paramref name="Anchor"/> is the token the excerpt should be centred on -
    /// the rarest one that was actually found, which is the part of the page a
    /// reader wants to see.
    /// </summary>
    private sealed record PageMatch(string MatchedText, int Strength, string? Anchor);

    /// <summary>
    /// A search plan for one issue, with each search word weighted by how rare
    /// it is IN THAT ISSUE.
    ///
    /// WHY WEIGHTS, AND WHY MEASURED RATHER THAN LISTED
    ///
    /// The old rule was "three of the four words appear somewhere on the page,
    /// as substrings". For "KARTIK TRADE MARKS COMPANY" that is a disaster:
    /// every page of the Trade Marks Journal carries the running head TRADE
    /// MARKS JOURNAL, and most carry a proprietor line containing COMPANY. Three
    /// of four words are therefore present on essentially every page of a
    /// 1,500-page issue with KARTIK appearing nowhere - so the search returned
    /// about 1,500 hits, each labelled "TRADE MARKS COMPANY", and the one real
    /// hit was indistinguishable from the noise. Substring matching made it
    /// worse: TRADE also matched inside TRADEMARKS, TRADERS and TRADED.
    ///
    /// A hard-coded stop-word list would help, but it cannot know that DELHI is
    /// boilerplate in one issue and distinctive in another. Document frequency
    /// can: a word appearing on most pages of this issue tells you nothing about
    /// which page you want, whatever that word is. So the weights are measured
    /// from the issue being searched, and the rule becomes "the rarest word you
    /// searched for must actually be there".
    /// </summary>
    private sealed record SearchPlan(
        string Needle,
        string NeedleCompact,
        List<string> Tokens,
        Dictionary<string, double> Weight,
        string? Rarest)
    {
        public double TotalWeight => Tokens.Sum(t => Weight[t]);
    }

    private static SearchPlan BuildPlan(string term, List<string> normalizedPages)
    {
        var needle = NormalizeForSearch(term);

        // Length >= 2, not > 2. The old filter dropped every short token, so a
        // search for "3M INDIA" collapsed to [INDIA] - one token, one required
        // match - and hit every page mentioning India.
        var tokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        var pageCount = Math.Max(1, normalizedPages.Count);

        // Word SETS, so a token is counted once per page and matching is on
        // whole words. This is also what stops TRADE matching inside TRADERS.
        var pageWords = normalizedPages
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .ToHashSet(StringComparer.Ordinal))
            .ToList();

        foreach (var token in tokens)
        {
            var df = pageWords.Count(w => w.Contains(token));

            // 1.0 for a word on no other page, falling to 0 for a word on every
            // page. Squared so that a word on a third of the pages is already
            // worth much less than one on a twentieth.
            var rarity = 1.0 - (double)df / pageCount;
            weight[token] = Math.Max(0.02, rarity * rarity);
        }

        var rarest = tokens.OrderByDescending(t => weight[t]).FirstOrDefault();

        return new SearchPlan(needle, Compact(needle), tokens, weight, rarest);
    }

    private static string Compact(string value) => value.Replace(" ", "");

    /// <summary>
    /// Scores one page against the plan. Returns null when the page is not a
    /// genuine hit.
    /// </summary>
    private static PageMatch? MatchPage(string normalizedPage, SearchPlan plan)
    {
        if (plan.Tokens.Count == 0) return null;

        // 1. The exact phrase, ON WORD BOUNDARIES. Padding both sides with a
        //    space is what makes this a word match rather than a substring one.
        //    Without it, a search for "TRADE" matched inside TRADERS, TRADED and
        //    TRADEMARKS - on every page of the issue, at full strength.
        if ((" " + normalizedPage + " ").Contains(" " + plan.Needle + " ", StringComparison.Ordinal))
            return new PageMatch(plan.Needle, 100, plan.Rarest);

        // 2. The phrase with all spacing removed. Journal typesetting breaks
        //    names across lines and columns, so "UNI-LEVER" split over a line
        //    end normalises to "UNI LEVER" and the exact test above misses it.
        //    Compacting both sides catches that without loosening word matching
        //    anywhere else.
        if (plan.NeedleCompact.Length >= 8 &&
            Compact(normalizedPage).Contains(plan.NeedleCompact, StringComparison.Ordinal))
            return new PageMatch(plan.Needle, 95, plan.Rarest);

        var words = normalizedPage.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var present = plan.Tokens.Where(words.Contains).ToList();
        if (present.Count == 0) return null;

        // 3. THE RAREST WORD MUST BE PRESENT. This is the whole fix. Whatever
        //    else a page contains, if the one word that actually distinguishes
        //    this search is not on it, the page is not a hit.
        if (plan.Rarest is not null && !words.Contains(plan.Rarest)) return null;

        // A single-word search is answered by rule 3 alone.
        if (plan.Tokens.Count == 1)
            return new PageMatch(present[0], 90, plan.Rarest);

        var covered = present.Sum(t => plan.Weight[t]);
        var coverage = plan.TotalWeight <= 0 ? 0 : covered / plan.TotalWeight;

        // 4. Weighted coverage. Missing COMPANY off a four-word name barely
        //    moves this; missing KARTIK makes it unreachable - and rule 3 has
        //    already rejected that page anyway.
        if (present.Count == plan.Tokens.Count)
            return new PageMatch(string.Join(' ', present), 85, plan.Rarest);

        if (coverage < 0.6) return null;

        return new PageMatch(string.Join(' ', present), (int)Math.Round(50 + 30 * coverage), plan.Rarest);
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
    private static string ExcerptAround(string pageText, string? anchor, SearchPlan plan)
    {
        // Anchor on the word that made this a hit, in rarity order.
        //
        // It used to anchor on the LONGEST token of the search term, whether or
        // not that token was one of the ones that matched. On a page matched
        // through TRADE / MARKS / COMPANY with KARTIK absent, the longest token
        // is COMPANY, whose first appearance is very likely an unrelated
        // proprietor hundreds of lines above - so the excerpt shown as evidence
        // for the hit was a different entry entirely.
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(anchor)) candidates.Add(anchor);
        candidates.AddRange(plan.Tokens.OrderByDescending(t => plan.Weight[t]));

        foreach (var candidate in candidates)
        {
            var index = pageText.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            return Trim(pageText, Math.Max(0, index - 500), 1400);
        }

        return Trim(pageText, 0, 1200);
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

        // The issue number is free text - JournalService.Add accepts whatever it
        // is given, and "2273/2026" is a plausible manual entry. Unsanitised,
        // Path.Combine turned that into a nested directory that does not exist
        // and threw DirectoryNotFoundException straight out of a UI handler.
        // DownloadPdfAsync already guards its filenames this way; this did not.
        var safeIssue = string.Concat((hit.IssueNumber ?? "unknown")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        var fileName = $"journal_{safeIssue}_p{hit.PageNumber}_extract.txt";
        var path = Path.Combine(outputDirectory, fileName);

        // The header used to read "Page {n}" above a body that is an EXCERPT -
        // about 1,400 characters around the match, often starting mid-sentence
        // with an ellipsis. Someone filing this with the docket as their record
        // of what was published would have been keeping a document that silently
        // omits the rest of the page, plausibly including the application number
        // and the other marks in the same entry. It now says what it is.
        var header =
            $"Journal {hit.IssueNumber} — published {hit.PublicationDate:dd MMM yyyy}\r\n" +
            $"Page {hit.PageNumber} — EXTRACT ONLY, not the full page\r\n" +
            $"Matched: {hit.MatchedText}  ({hit.MatchQuality})\r\n" +
            $"Source PDF: {hit.PdfPath}\r\n" +
            (hit.FromOcr
                ? "Text obtained by OCR — verify against the PDF before relying on it.\r\n"
                : "Text taken from the PDF text layer (exact).\r\n") +
            "This file holds the text surrounding the match, not the whole page. " +
            "Open the source PDF at the page above for the complete entry.\r\n" +
            new string('-', 70) + "\r\n\r\n";

        File.WriteAllText(path, header + hit.PageExcerpt);
        return path;
    }
}
