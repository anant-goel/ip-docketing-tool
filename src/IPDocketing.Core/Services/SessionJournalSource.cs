using HtmlAgilityPack;

namespace IPDocketing.Core.Services;

/// <summary>
/// Journal source backed by <see cref="WebFormsSession"/> - a real session with
/// postback replay, and no browser required.
///
/// Why it comes first in the chain: it needs no WebView2, no UI thread and no
/// window, so it works in the background sync, runs in a fraction of the time,
/// and cannot paint itself over the app. The embedded browser stays as the
/// fallback for anything script-driven that this cannot see.
///
/// It handles both link shapes on the listing:
///   - a real href, downloaded with the session's cookies attached
///   - a __doPostBack target, replayed as a form POST
///
/// Which shape each link turned out to be is reported in its label, so a run
/// that finds nothing says which case it was in rather than leaving it to be
/// inferred.
/// </summary>
public sealed class SessionJournalSource : IJournalSource, IDisposable
{
    private const string ListingUrl = "https://search.ipindia.gov.in/IPOJournal/Journal/Trademark";

    private readonly WebFormsSession _session = new();

    // Postback targets discovered on the last listing load, keyed by the
    // element index handed out in JournalSourceLink.
    private readonly Dictionary<int, WebFormsSession.PostbackTarget> _postbacks = new();

    public string SourceName => "Session client (cookies + postback replay)";

    public Action<string>? Progress { get; set; }

    public string DescribeCookies() => _session.DescribeCookies(ListingUrl);

    public async Task<List<JournalSourceIssue>> ListIssuesAsync(CancellationToken ct = default)
    {
        Progress?.Invoke("Opening the listing with a session...");

        var html = await _session.OpenAsync(ListingUrl, ct);

        _postbacks.Clear();

        // Reported, not used for matching. How many postback links the page
        // carries is the single most useful number when a run comes back thin:
        // zero means the targets are written by script at click time and only
        // the browser can reach them.
        var postbackTargets = _session.FindPostbackTargets();
        Progress?.Invoke($"{postbackTargets.Count} postback target(s) on the listing.");

        var document = new HtmlDocument();
        document.LoadHtml(html);

        // Index over anchors in document order, so a postback target found by
        // the session lines up with the link recorded here.
        // A plain list rather than new HtmlNodeCollection(null) - that ctor
        // takes a parent node and passing null is not something to rely on.
        var anchorNodes = document.DocumentNode.SelectNodes("//a");
        var anchorList = anchorNodes?.ToList() ?? new List<HtmlNode>();

        var issues = new List<JournalSourceIssue>();
        var rows = document.DocumentNode.SelectNodes("//tr");
        if (rows is null) return issues;

        var postbackIndex = 0;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            if (cells is null || cells.Count < 3) continue;

            string? number = null;
            DateTime? date = null;

            foreach (var cell in cells)
            {
                var text = HtmlEntity.DeEntitize(cell.InnerText ?? "").Trim();

                if (number is null && System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{3,5}$"))
                {
                    number = text;
                    continue;
                }

                if (date is null)
                {
                    var parsed = ParseDate(text);
                    if (parsed is not null) date = parsed;
                }
            }

            if (number is null) continue;

            var links = new List<JournalSourceLink>();
            var rowAnchors = row.SelectNodes(".//a");

            if (rowAnchors is not null)
            {
                foreach (var anchor in rowAnchors)
                {
                    var index = anchorList.IndexOf(anchor);
                    if (index < 0) index = postbackIndex;

                    var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", "") ?? "").Trim();
                    var isPostback = href.Contains("__doPostBack", StringComparison.OrdinalIgnoreCase) ||
                                     (anchor.GetAttributeValue("onclick", "") ?? "")
                                         .Contains("__doPostBack", StringComparison.OrdinalIgnoreCase);

                    string? url = null;
                    if (!isPostback && href.Length > 0 &&
                        !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
                        !href.StartsWith("#"))
                    {
                        url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? href
                            : new Uri(new Uri(ListingUrl), href).ToString();
                    }

                    var label = DeriveLabel(anchor, links.Count);

                    if (isPostback)
                    {
                        // THE TARGET IS READ FROM THIS ANCHOR, NOT MATCHED BY LABEL.
                        //
                        // This used to pair the link against the separately
                        // computed postbackTargets list by comparing LABELS -
                        // and that cannot work for the links it exists to
                        // handle. These are icon links with no text, so both
                        // label functions fall through to a positional
                        // placeholder, and the two placeholders do not even use
                        // the same wording ("Link 3" here against "Download 3"
                        // there) or the same ordinal, since one counts anchors
                        // across the whole document and the other counts links
                        // within a row. The match therefore fails, no entry is
                        // written to _postbacks, and the link is reported
                        // without a target - which surfaces as "issues found,
                        // links not usable", the exact symptom this whole source
                        // was written to end.
                        //
                        // The anchor already carries its own __doPostBack call.
                        // Reading it from there needs no correlation at all.
                        var target = ExtractPostbackTarget(anchor, label);

                        if (target is not null)
                        {
                            _postbacks[index] = target;
                            label = $"{label} [postback]";
                        }
                    }

                    links.Add(new JournalSourceLink(label, url, index));
                    postbackIndex++;
                }
            }

            issues.Add(new JournalSourceIssue(number, date, links));
        }

        return issues;
    }

    public async Task<JournalDownloadResult> DownloadAsync(
        JournalSourceIssue issue, JournalSourceLink link, string targetPath,
        CancellationToken ct = default)
    {
        try
        {
            // Real URL: straight download, with the session's cookies.
            if (link.Url is not null)
            {
                Progress?.Invoke($"Downloading {link.Label}...");
                var direct = await _session.DownloadAsync(link.Url, targetPath, ct);
                return direct.IsFile
                    ? JournalDownloadResult.Success(direct.SavedPath!, direct.Bytes)
                    : JournalDownloadResult.Failure(direct.Error ?? "download failed");
            }

            // Postback: replay the form submission the page's own script would.
            if (_postbacks.TryGetValue(link.ElementIndex, out var target))
            {
                Progress?.Invoke($"Replaying postback for {link.Label}...");
                var posted = await _session.PostBackAsync(
                    target.EventTarget, target.EventArgument, targetPath, ct);

                return posted.IsFile
                    ? JournalDownloadResult.Success(posted.SavedPath!, posted.Bytes)
                    : JournalDownloadResult.Failure(posted.Error ?? "postback returned no file");
            }

            return JournalDownloadResult.Failure(
                "This link has no URL and no __doPostBack target in the markup - its target is " +
                "computed by script at click time, so only the embedded browser can action it.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JournalDownloadResult.Failure(ex.Message);
        }
    }

    public async Task<List<JournalDownloadResult>> DownloadIssueAsync(
        string journalNumber, string targetDirectory, int maxFiles = 8,
        CancellationToken ct = default)
    {
        var results = new List<JournalDownloadResult>();
        Directory.CreateDirectory(targetDirectory);

        var issues = await ListIssuesAsync(ct);
        var issue = issues.FirstOrDefault(i =>
            string.Equals(i.JournalNumber, journalNumber, StringComparison.OrdinalIgnoreCase));

        if (issue is null)
        {
            results.Add(JournalDownloadResult.Failure(
                $"Journal {journalNumber} is not on the current listing."));
            return results;
        }

        if (issue.Links.Count == 0)
        {
            results.Add(JournalDownloadResult.Failure(
                $"Journal {journalNumber} was found but carries no links."));
            return results;
        }

        foreach (var link in issue.Links.Take(maxFiles))
        {
            ct.ThrowIfCancellationRequested();

            var safe = string.Concat(link.Label.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            if (safe.Length > 50) safe = safe[..50];

            var target = Path.Combine(targetDirectory, $"journal_{journalNumber}_{safe}.pdf");

            if (File.Exists(target) && new FileInfo(target).Length > 20_000)
            {
                results.Add(JournalDownloadResult.Success(target, new FileInfo(target).Length));
                continue;
            }

            results.Add(await DownloadAsync(issue, link, target, ct));

            // A postback changes the page state, so the listing is reloaded
            // before the next one - the same reason the browser path does it.
            if (link.Url is null) await ListIssuesAsync(ct);
        }

        return results;
    }

    /// <summary>
    /// The __doPostBack target and argument written on this anchor, from either
    /// href or onclick. Returns null when the anchor is not a postback link.
    /// </summary>
    private static WebFormsSession.PostbackTarget? ExtractPostbackTarget(HtmlNode anchor, string label)
    {
        var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", "") ?? "");
        var onclick = HtmlEntity.DeEntitize(anchor.GetAttributeValue("onclick", "") ?? "");

        var pattern = new System.Text.RegularExpressions.Regex(
            @"__doPostBack\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]*)['""]\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = pattern.Match(href);
        if (!match.Success) match = pattern.Match(onclick);
        if (!match.Success) return null;

        return new WebFormsSession.PostbackTarget(
            label, match.Groups[1].Value, match.Groups[2].Value);
    }

    private static string DeriveLabel(HtmlNode anchor, int ordinal)
    {
        var candidates = new List<string?>
        {
            anchor.InnerText,
            anchor.GetAttributeValue("title", null),
            anchor.GetAttributeValue("aria-label", null),
        };

        var image = anchor.SelectSingleNode(".//img");
        if (image is not null)
        {
            candidates.Add(image.GetAttributeValue("alt", null));
            candidates.Add(image.GetAttributeValue("title", null));
        }

        // The image's own filename, which on this listing is usually the only
        // thing that names the class range.
        var src = image?.GetAttributeValue("src", "") ?? "";
        if (src.Length > 0)
            candidates.Add(src.Split('/', '\\').LastOrDefault()?.Split('.').FirstOrDefault());

        candidates.Add(anchor.GetAttributeValue("href", null));

        var usable = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => System.Text.RegularExpressions.Regex
                .Replace(HtmlEntity.DeEntitize(c!), @"\s+", " ").Trim())
            .Where(c => c.Length is > 0 and < 120)
            .ToList();

        // A label that names a class beats one that merely exists. The anchor's
        // own text is "PDF" on every one of these links, and taking it means the
        // class range is never known - the same defect that was fixed in
        // JournalFetchService.DeriveLabel.
        var named = usable.FirstOrDefault(c => JournalFetchService.TryParseClassRange(c, out _, out _));
        if (named is not null) return named;

        return usable.FirstOrDefault() ?? $"Download {ordinal + 1}";
    }

    private static DateTime? ParseDate(string text)
    {
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy" };
        foreach (var format in formats)
            if (DateTime.TryParseExact(text.Trim(), format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed.Date;
        return null;
    }

    public void Dispose() => _session.Dispose();
}
