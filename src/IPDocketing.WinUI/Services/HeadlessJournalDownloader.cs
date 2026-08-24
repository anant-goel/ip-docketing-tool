using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace IPDocketing.WinUI.Services;

/// <summary>
/// Downloads Journal PDFs by driving a hidden browser: load the listing page,
/// click a link, and catch the download the click produces.
///
/// WHY THIS, AFTER THE HTTP APPROACH FAILED
///
/// Every previous attempt read the page's HTML and tried to recover a URL from
/// each anchor's href. That kept yielding zero links, and the most likely
/// reason is that these anchors have no URL to recover: an ASP.NET WebForms
/// grid renders them as __doPostBack handlers, where the PDF only exists as the
/// response to a form submission carrying __VIEWSTATE and __EVENTVALIDATION.
/// There is no address to GET. No amount of better regex fixes that.
///
/// A browser doesn't have that problem, because it does what a person does: it
/// runs the JavaScript, submits the form with the right hidden fields, and
/// receives a file. WebView2 then hands that file to
/// <see cref="CoreWebView2.DownloadStarting"/>, where the destination path can
/// be set directly - so the file is written where we want it, under the name we
/// want, without a Save dialog and without ever showing a window.
///
/// This is also strictly less fragile than URL scraping: it survives the
/// Registry changing its URL scheme, its control ids, or its postback wiring,
/// because it only depends on the link's visible text.
///
/// It is NOT a way around anything. The listing page is public and has no
/// CAPTCHA, no login and no rate gate - see JournalFetchService. This is the
/// same public file a browser fetches when you click the link, obtained the
/// same way.
/// </summary>
public sealed class HeadlessJournalDownloader : IDisposable
{
    private readonly WebView2 _browser;
    private readonly DispatcherQueue _dispatcher;
    private readonly Panel _host;

    private TaskCompletionSource<bool>? _navigationDone;
    private TaskCompletionSource<string?>? _downloadDone;
    private string? _pendingTargetPath;

    private const string ListingUrl = "https://search.ipindia.gov.in/IPOJournal/Journal/Trademark";

    /// <summary>Progress messages, raised on the UI thread.</summary>
    public event Action<string>? Progress;

    /// <summary>Where the host is parked when the browser is meant to be unseen.</summary>
    public static readonly Thickness OffScreen = new(-4000, -4000, 0, 0);

    /// <summary>
    /// The host panel must be in the visual tree, and at a real layout size, for
    /// WebView2 to initialise.
    ///
    /// BUG FIX: hiding it used to mean Opacity = 0 on both the host and the
    /// browser. WebView2 does not render into the XAML visual tree - it is
    /// composited in a layer of its own - so it ignores Opacity entirely, and
    /// ignores Canvas.ZIndex with it. The result was the IP India listing page
    /// painted straight over the Journal page during every background fetch.
    ///
    /// Position is the one thing the composited layer does follow, so the host
    /// is parked off the top-left of the window instead. That genuinely hides
    /// it, and costs nothing: the control still has its 900x700 layout box and
    /// still initialises.
    /// </summary>
    public HeadlessJournalDownloader(Panel host, bool visible = false)
    {
        _host = host;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _visible = visible;

        _browser = new WebView2
        {
            Width = 900,
            Height = 700,
            IsHitTestVisible = visible
        };

        _host.Children.Add(_browser);

        _host.Width = 900;
        _host.Height = 700;
        _host.IsHitTestVisible = visible;

        if (visible)
        {
            // Visible mode exists because watching it fail is far faster than
            // inferring the failure from a log. Hidden is the default.
            _host.Margin = new Thickness(0);
            _host.HorizontalAlignment = HorizontalAlignment.Center;
            _host.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            _host.Margin = OffScreen;
            _host.HorizontalAlignment = HorizontalAlignment.Left;
            _host.VerticalAlignment = VerticalAlignment.Top;
        }
    }

    private readonly bool _visible;

    /// <summary>
    /// The spellings the listing might use for one date. Compared with all
    /// whitespace stripped, so "17/08/2026" and "17 / 08 / 2026" both match.
    /// </summary>
    private static string[] DateCandidates(DateTime? date)
    {
        if (date is not { } d) return Array.Empty<string>();

        // InvariantCulture, on every one of these. Without it these are
        // CURRENT-CULTURE format strings, and "/" in a .NET format string is not
        // a slash - it is the culture's date separator placeholder. On a German
        // machine "dd/MM/yyyy" renders 17.08.2026, on a French one "dd-MMM-yyyy"
        // renders 17-août-2026, and on a Thai one the year comes out 2569.
        // cellMatches compares these for exact equality against the page's
        // "17/08/2026", so every row failed to match, ListLinksAsync returned an
        // empty list, and the user was told the issue has no links - with
        // nothing anywhere to suggest the machine's locale was the reason.
        // JournalFetchService.ParseDate was fixed for exactly this; this half
        // was missed.
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        return new[]
        {
            d.ToString("dd/MM/yyyy", inv),
            d.ToString("d/M/yyyy", inv),
            d.ToString("dd-MM-yyyy", inv),
            d.ToString("dd.MM.yyyy", inv),
            d.ToString("ddMMMyyyy", inv),
            d.ToString("dd-MMM-yyyy", inv),
            d.ToString("dd MMM yyyy", inv),
            d.ToString("MMM d, yyyy", inv),
        };
    }

    private void Report(string message) => Progress?.Invoke(message);

    /// <summary>True when the file actually begins with "%PDF-".</summary>
    private static bool StartsWithPdfHeader(string path)
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
    /// One download link in an issue's row.
    ///
    /// <paramref name="Href"/> is the actual resolved URL where the anchor has
    /// one, and it is what gets used. <paramref name="Index"/> is a fallback for
    /// genuine postback links that have no URL at all - and it is only a
    /// fallback, because a document-wide index is not a stable address across a
    /// re-navigation.
    /// </summary>
    public sealed record LinkInfo(int Index, string Label, string? Href = null);

    public sealed record DownloadOutcome(
        string Label,
        bool Saved,
        string? FilePath,
        long Bytes,
        string? Error);

    private async Task EnsureReadyAsync()
    {
        if (_browser.CoreWebView2 is not null) return;

        // BUG FIX: a 1x1 WebView2 does not reliably initialise - the control
        // needs a real layout size, and several WebView2 builds simply never
        // complete EnsureCoreWebView2Async on a zero-area element. It is given
        // a genuine size here and hidden with Opacity instead, which keeps it
        // invisible without lying to the layout system.
        _browser.Width = 900;
        _browser.Height = 700;

        // A hang here previously looked like "background processing does
        // nothing": no window, no error, no result. Now it fails loudly.
        var init = _browser.EnsureCoreWebView2Async().AsTask();
        var finished = await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(45)));

        if (finished != init)
            throw new InvalidOperationException(
                "The hidden browser did not start within 45 seconds. The WebView2 runtime may be " +
                "missing - on ARM64 it must be the ARM64 runtime. Install it from " +
                "developer.microsoft.com/microsoft-edge/webview2.");

        await init;

        if (_browser.CoreWebView2 is null)
            throw new InvalidOperationException("The hidden browser reported ready but produced no CoreWebView2.");

        var core = _browser.CoreWebView2;

        core.NavigationCompleted += (_, e) => _navigationDone?.TrySetResult(e.IsSuccess);

        // Links that would open a new window are kept in this one - otherwise
        // the click spawns a popup we never see and the download never arrives.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        core.DownloadStarting += OnDownloadStarting;

        // No need to paint anything we never show.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
    }

    /// <summary>
    /// Redirects the download to our own path and suppresses the download UI.
    /// This is the whole trick: the file never lands in Downloads and there is
    /// no Save As dialog, so the run is genuinely unattended.
    /// </summary>
    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            // Hides the download popup that would otherwise appear.
            args.Handled = true;

            if (_pendingTargetPath is not null)
                args.ResultFilePath = _pendingTargetPath;

            var operation = args.DownloadOperation;
            var path = args.ResultFilePath;

            // THE WAIT THIS DOWNLOAD BELONGS TO, captured now.
            //
            // Settle used to read the _downloadDone FIELD at the moment it
            // fired, which meant a download from an ABANDONED wait could
            // complete a LATER one. Concretely: link A stalls and times out,
            // link B starts and installs a fresh TCS, then A's download
            // finally completes and hands A's file to B's waiter. B is
            // reported as saved, with A's bytes, under B's label - the class
            // 1-9 PDF recorded and searched as class 26-34, showing green.
            // Capturing the TCS pins each download to the wait that started it,
            // so a late arrival resolves nothing.
            var waiter = _downloadDone;

            operation.StateChanged += (_, _) => Settle(operation, path, waiter);

            // BUG FIX: a small or cached file can finish before this handler is
            // attached, so StateChanged never fires again and the wait times
            // out with "no download started" - even though the file is already
            // on disk. Checking the current state immediately closes that race.
            Settle(operation, path, waiter);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DownloadStarting failed: {ex}");
            _downloadDone?.TrySetResult(null);
        }
    }

    private void Settle(CoreWebView2DownloadOperation operation, string path,
                        TaskCompletionSource<string?>? waiter)
    {
        if (waiter is null) return;

        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                waiter.TrySetResult(path);
                break;

            case CoreWebView2DownloadState.Interrupted:
                waiter.TrySetResult(null);
                break;
        }
    }

    private async Task<bool> NavigateAsync(string url, TimeSpan timeout)
    {
        _navigationDone = new TaskCompletionSource<bool>();
        _browser.CoreWebView2.Navigate(url);

        var completed = await Task.WhenAny(_navigationDone.Task, Task.Delay(timeout));
        return completed == _navigationDone.Task && _navigationDone.Task.Result;
    }

    private async Task<string> RunScriptAsync(string script)
    {
        var raw = await _browser.CoreWebView2.ExecuteScriptAsync(script);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return "";
        return JsonSerializer.Deserialize<string>(raw) ?? "";
    }

    /// <summary>
    /// Lists the download links for one journal issue, as rendered in a real
    /// browser - so JavaScript-generated rows exist by the time we look.
    /// Anchors are identified by their position in the document, because that
    /// index is what we later click; the label is only for naming the file.
    /// </summary>
    /// <summary>
    /// Lists the download links for one journal issue, located by its number,
    /// its publication date, or both.
    ///
    /// Matching by DATE matters because that is how the row is identified in
    /// practice: you know the issue was published on 17 Aug 2026 long before you
    /// know it is issue 2274. Passing both is best - either one identifies the
    /// row, and having two means a renumbering or a reformatted date does not
    /// lose it.
    /// </summary>
    public async Task<List<LinkInfo>> ListLinksAsync(string? journalNumber, DateTime? publicationDate = null)
    {
        await EnsureReadyAsync();

        Report("Opening the Journal listing...");
        if (!await NavigateAsync(ListingUrl, TimeSpan.FromSeconds(45)))
            throw new InvalidOperationException("The Journal listing page did not load.");

        // Give any deferred rendering a moment to settle.
        await Task.Delay(1500);

        var script = $$"""
            (function () {
                var wanted = {{JsonSerializer.Serialize(journalNumber ?? "")}};
                var wantedDates = {{JsonSerializer.Serialize(DateCandidates(publicationDate))}};

                function squash(s) {
                    return (s || '').replace(/\u00a0/g, ' ').replace(/\s+/g, '').toUpperCase();
                }

                function matchesNumber(text) {
                    var t = squash(text);
                    return !!(wanted && t === squash(wanted));
                }

                function matchesDate(text) {
                    var t = squash(text);
                    if (!t) return false;
                    for (var d = 0; d < wantedDates.length; d++) {
                        // Both sides squashed. The cell text had its whitespace
                        // stripped while the candidates kept theirs, so any
                        // candidate containing a space - "17 Aug 2026",
                        // "Aug 17, 2026" - could never match anything.
                        if (t === squash(wantedDates[d])) return true;
                    }
                    return false;
                }

                // Index over the same widened selector used when clicking, so
                // the index recorded here still addresses the right element.
                var all = Array.prototype.slice.call(document.querySelectorAll(
                    'a, input[type="image"], input[type="submit"], input[type="button"], button, img[onclick]'));
                var out = [];

                function labelFor(el, ordinal) {
                    var candidates = [
                        (el.innerText || '').trim(),
                        el.getAttribute('title') || '',
                        el.getAttribute('alt') || '',
                        el.getAttribute('aria-label') || '',
                        el.value || ''
                    ];

                    // An <img> inside the link usually carries the only
                    // human-readable name, in its alt or title.
                    var img = el.querySelector ? el.querySelector('img') : null;
                    if (img) {
                        candidates.push(img.getAttribute('alt') || '');
                        candidates.push(img.getAttribute('title') || '');
                        var src = img.getAttribute('src') || '';
                        var srcName = src.split('/').pop().split('.')[0];
                        if (srcName) candidates.push(srcName);
                    }

                    // Last resort: a filename out of the href.
                    var href = el.getAttribute('href') || '';
                    var m = href.match(/([^\/\\?#]+)\.(pdf|zip)/i);
                    if (m) candidates.push(m[1]);

                    for (var c = 0; c < candidates.length; c++) {
                        var v = (candidates[c] || '').replace(/\s+/g, ' ').trim();
                        if (v.length > 0 && v.length < 120) return v;
                    }

                    // Named by position rather than skipped. A link with no
                    // name is still a link.
                    return 'Download ' + (ordinal + 1);
                }

                var rows = document.querySelectorAll('tr');
                for (var r = 0; r < rows.length; r++) {
                    var cells = rows[r].querySelectorAll('td');
                    if (cells.length < 3) continue;

                    // Match the issue by its number cell, not by row position.
                    // Scan the first four cells, not three: the row is
                    // [Sr.No | Journal No | Date of Publication | Date of
                    // Availability | Download...], so a match on the publication
                    // date lives in cell 2 and the availability date in cell 3.
                    // WHEN A JOURNAL NUMBER IS GIVEN, IT DECIDES.
                    //
                    // This used to be a plain OR across the first four cells, so
                    // a match on ANY of them - including cell 3, the Date of
                    // Availability - selected the row. Journal 2273 is published
                    // on the 10th and available on the 17th; journal 2274 is
                    // published on the 17th. Ask for 2274 by number AND date and
                    // the 2273 row matched first on its availability cell, and
                    // its links were returned as 2274's. The wrong week's PDFs
                    // were then downloaded under the requested issue's name, and
                    // nothing ever re-checked the row.
                    var numberMatched = false;
                    var dateMatched = false;

                    for (var c = 0; c < Math.min(4, cells.length); c++) {
                        if (matchesNumber(cells[c].innerText)) numberMatched = true;
                        if (matchesDate(cells[c].innerText)) dateMatched = true;
                    }

                    // A number, when supplied, must match. The date alone is
                    // only allowed to identify the row when no number was given.
                    var isRow = wanted ? numberMatched : dateMatched;
                    if (!isRow) continue;

                    // The Download column holds ICON links - an <img> inside an
                    // <a>, with no text of its own. Requiring innerText
                    // discarded every one of them, which is why the row parsed
                    // and the links did not. Text is now preferred but never
                    // required; a name is derived from whatever the element
                    // does carry.
                    var clickables = rows[r].querySelectorAll(
                        'a, input[type="image"], input[type="submit"], input[type="button"], button, img[onclick]');

                    for (var a = 0; a < clickables.length; a++) {
                        var el = clickables[a];
                        var label = labelFor(el, a);
                        // The resolved href matters far more than the index.
                        // el.href on an anchor is already absolute; anything
                        // javascript: or empty is not a URL we can navigate to.
                        var href = '';
                        try {
                            var raw = el.getAttribute ? (el.getAttribute('href') || '') : '';
                            if (raw && raw.indexOf('javascript:') !== 0 && raw.charAt(0) !== '#') {
                                href = el.href || raw;
                            }
                        } catch (e) { }

                        out.push({ index: all.indexOf(el), label: label, href: href, tag: el.tagName });
                    }
                    break;
                }

                return JSON.stringify(out);
            })();
            """;

        var json = await RunScriptAsync(script);
        if (string.IsNullOrWhiteSpace(json)) return new List<LinkInfo>();

        using var parsed = JsonDocument.Parse(json);
        var links = new List<LinkInfo>();

        foreach (var item in parsed.RootElement.EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            var label = item.GetProperty("label").GetString() ?? "";
            var href = item.TryGetProperty("href", out var h) ? h.GetString() : null;

            if (index >= 0 || !string.IsNullOrWhiteSpace(href))
                links.Add(new LinkInfo(index, label, string.IsNullOrWhiteSpace(href) ? null : href));
        }

        return links;
    }

    /// <summary>
    /// Clicks one link and saves whatever it downloads to <paramref name="targetPath"/>.
    ///
    /// A click that produces no download within the timeout is reported as
    /// such rather than hanging - some links in these rows are notices that
    /// navigate rather than download, and one of those must not stall a batch.
    /// </summary>
    public async Task<DownloadOutcome> DownloadLinkAsync(
        LinkInfo link, string targetPath, TimeSpan timeout)
    {
        await EnsureReadyAsync();

        _pendingTargetPath = targetPath;
        _downloadDone = new TaskCompletionSource<string?>();

        // GO STRAIGHT TO THE URL WHERE THERE IS ONE.
        //
        // Clicking by document-wide index was the mistake. The index is captured
        // while listing the row, then ReturnToListingAsync re-navigates before
        // the next click - and any difference in the rebuilt DOM shifts every
        // index after it. Click the wrong anchor and you reach ViewJournal with
        // the wrong FileName, or none, which is precisely how their server comes
        // back with "The UNC path should be of the form \\server\share": the
        // path it built was empty. Clicking the same link by hand works because
        // a person clicks the link, not the eleventh anchor on the page.
        //
        // The href captured at listing time IS the address the manual click
        // goes to, so navigating to it does exactly what you do by hand.
        if (!string.IsNullOrWhiteSpace(link.Href))
        {
            Report($"Opening \"{link.Label}\"...");

            _navigationDone = new TaskCompletionSource<bool>();
            _browser.CoreWebView2.Navigate(link.Href);
        }
        else
        {
            // A genuine postback with no URL. Clicking is the only option, but
            // the element is verified against the label we recorded first -
            // downloading the wrong class range silently is worse than failing.
            Report($"Clicking \"{link.Label}\"...");

            var clickScript = $$"""
                (function () {
                    var all = document.querySelectorAll(
                        'a, input[type="image"], input[type="submit"], input[type="button"], button, img[onclick]');
                    var el = all[{{link.Index}}];
                    if (!el) return "no-element";

                    var expected = {{JsonSerializer.Serialize(link.Label)}};

                    // The label was derived from the <img> INSIDE the anchor -
                    // its alt, title or filename - because these are icon links
                    // with no text of their own. This check only looked at the
                    // anchor's own attributes, which an <a> never carries, so
                    // `actual` was the empty string and EVERY genuine postback
                    // icon link was rejected as "moved" - blaming a race that
                    // had not happened. The image is now read too.
                    var img = el.querySelector ? el.querySelector('img') : null;
                    var imgSrc = img ? (img.getAttribute('src') || '') : '';

                    var actual = ((el.innerText || '') + ' ' + (el.value || '') + ' ' +
                                  (el.getAttribute('title') || '') + ' ' +
                                  (el.getAttribute('alt') || '') + ' ' +
                                  (el.getAttribute('href') || '') + ' ' +
                                  (img ? (img.getAttribute('alt') || '') + ' ' +
                                         (img.getAttribute('title') || '') + ' ' +
                                         imgSrc : '')).replace(/\s+/g, ' ').trim();

                    // Only insist when we recorded a real name. Icon links are
                    // named "Download 3" by position, and checking those against
                    // the element would reject every one of them.
                    if (expected.indexOf('Download ') !== 0 &&
                        actual.toLowerCase().indexOf(expected.toLowerCase()) === -1) {
                        return "moved";
                    }

                    el.scrollIntoView();
                    el.click();
                    return "clicked";
                })();
                """;

            var clickResult = await RunScriptAsync(clickScript);

            if (clickResult == "moved")
                return new DownloadOutcome(link.Label, false, null, 0,
                    "The listing rearranged between reading it and clicking, so the link is no longer " +
                    "where it was. Nothing was downloaded rather than risk fetching the wrong range.");

            if (clickResult != "clicked")
                return new DownloadOutcome(link.Label, false, null, 0, "The link could not be found on the page.");
        }

        var waiter = _downloadDone;
        var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout));

        // Both cleared together. Leaving _downloadDone pointing at an abandoned
        // wait is how a late download used to settle the next link's result.
        _pendingTargetPath = null;
        _downloadDone = null;

        if (finished != waiter.Task)
        {
            // No file arrived. Before reporting a timeout, look at what the page
            // actually became - because the interesting case is that the click
            // worked perfectly and the Registry's own server then failed.
            //
            // Their journal viewer is an ASP.NET MVC app whose ViewJournal action
            // builds a UNC path out of the FileName it is given, and it throws
            // "The UNC path should be of the form \\server\\share" straight
            // onto the page when that path does not resolve. That is a fault on
            // their file server, not a fault in this click, and it is completely
            // invisible from here unless the page is read back. Reporting it as
            // "no download started" sends you looking in the wrong place.
            var diagnosis = await DescribeCurrentPageAsync();

            return new DownloadOutcome(link.Label, false, null, 0, diagnosis);
        }

        var path = waiter.Task.Result;
        if (path is null || !File.Exists(path))
            return new DownloadOutcome(link.Label, false, null, 0, "The download was interrupted.");

        var info = new FileInfo(path);

        // A tiny file is an error page saved with a .pdf name, not a Journal.
        if (info.Length < 20_000)
        {
            try { File.Delete(path); } catch { }
            return new DownloadOutcome(link.Label, false, null, info.Length,
                $"Only {info.Length} bytes arrived - that is an error page, not the Journal.");
        }

        // Size alone was standing in for "is this a PDF", and a large HTML error
        // page passes that test comfortably. The header is the actual answer.
        if (!StartsWithPdfHeader(path))
        {
            try { File.Delete(path); } catch { }
            return new DownloadOutcome(link.Label, false, null, info.Length,
                $"{info.Length:N0} bytes arrived but the file is not a PDF - it has no PDF header. " +
                "The Registry most likely served an error page.");
        }

        return new DownloadOutcome(link.Label, true, path, info.Length, null);
    }

    /// <summary>
    /// Reads back what the browser is showing, so a failed click can say what
    /// actually happened rather than only that nothing downloaded.
    /// </summary>
    private async Task<string> DescribeCurrentPageAsync()
    {
        try
        {
            var probe = await RunScriptAsync("""
                (function () {
                    var body = (document.body && document.body.innerText) || '';
                    var trimmed = body.replace(/\s+/g, ' ').trim();

                    var serverError = /Server Error in|Runtime Error|HTTP Error \d/i.test(trimmed);
                    var uncFault = /UNC path should be of the form/i.test(trimmed);

                    return JSON.stringify({
                        url: location.href,
                        title: document.title || '',
                        serverError: serverError,
                        uncFault: uncFault,
                        excerpt: trimmed.slice(0, 300)
                    });
                })();
                """);

            if (string.IsNullOrWhiteSpace(probe))
                return "No download started within the timeout, and the page could not be read back.";

            using var parsed = JsonDocument.Parse(probe);
            var root = parsed.RootElement;

            var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var uncFault = root.TryGetProperty("uncFault", out var f) && f.ValueKind == JsonValueKind.True;
            var serverError = root.TryGetProperty("serverError", out var e) && e.ValueKind == JsonValueKind.True;
            var excerpt = root.TryGetProperty("excerpt", out var x) ? x.GetString() ?? "" : "";

            if (uncFault)
                return "IP India's viewer was reached with no usable FileName, so it could not build a " +
                       "path and threw. Clicking the same link by hand works, which means the request " +
                       "went out wrong rather than their file server being down - almost always the " +
                       "wrong link being opened. " + (url.Length > 0 ? $"Request: {url}" : "");

            if (serverError)
                return $"IP India's server returned an error page instead of the PDF. {excerpt}";

            return "No download started within the timeout - this link navigates rather than downloading. " +
                   (url.Length > 0 ? $"It went to: {url}" : "");
        }
        catch
        {
            return "No download started within the timeout, and reading the page back also failed.";
        }
    }

    /// <summary>
    /// Returns to the listing between downloads. A postback link usually leaves
    /// the page in a changed state, so re-navigating keeps each click starting
    /// from the same place.
    /// </summary>
    public async Task ReturnToListingAsync()
    {
        await NavigateAsync(ListingUrl, TimeSpan.FromSeconds(45));
        await Task.Delay(1200);
    }

    public void Dispose()
    {
        try
        {
            if (_browser.CoreWebView2 is not null)
                _browser.CoreWebView2.DownloadStarting -= OnDownloadStarting;

            _host.Children.Remove(_browser);
            _browser.Close();

            // Park the host again so a visible diagnostic run never leaves a
            // 900x700 hole in the middle of the page after it finishes.
            _host.Margin = OffScreen;
            _host.IsHitTestVisible = false;
            _host.HorizontalAlignment = HorizontalAlignment.Left;
            _host.VerticalAlignment = VerticalAlignment.Top;
        }
        catch
        {
            // Teardown of a hidden helper is never worth surfacing.
        }
    }
}
