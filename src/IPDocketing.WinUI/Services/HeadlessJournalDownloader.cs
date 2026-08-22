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

    /// <summary>
    /// The host panel must be in the visual tree for WebView2 to initialise,
    /// but it can be zero-sized and transparent - which is how this stays
    /// invisible while still being a real browser.
    /// </summary>
    public HeadlessJournalDownloader(Panel host)
    {
        _host = host;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _browser = new WebView2
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false
        };

        _host.Children.Add(_browser);
    }

    private void Report(string message) => Progress?.Invoke(message);

    public sealed record LinkInfo(int Index, string Label);

    public sealed record DownloadOutcome(
        string Label,
        bool Saved,
        string? FilePath,
        long Bytes,
        string? Error);

    private async Task EnsureReadyAsync()
    {
        if (_browser.CoreWebView2 is not null) return;

        // Same user-data folder as the visible browser, so a session the person
        // has already established is reused rather than started fresh.
        await _browser.EnsureCoreWebView2Async();

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

            operation.StateChanged += (_, _) =>
            {
                switch (operation.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        _downloadDone?.TrySetResult(path);
                        break;

                    case CoreWebView2DownloadState.Interrupted:
                        _downloadDone?.TrySetResult(null);
                        break;
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DownloadStarting failed: {ex}");
            _downloadDone?.TrySetResult(null);
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
    public async Task<List<LinkInfo>> ListLinksAsync(string journalNumber)
    {
        await EnsureReadyAsync();

        Report("Opening the Journal listing...");
        if (!await NavigateAsync(ListingUrl, TimeSpan.FromSeconds(45)))
            throw new InvalidOperationException("The Journal listing page did not load.");

        // Give any deferred rendering a moment to settle.
        await Task.Delay(1500);

        var script = $$"""
            (function () {
                var wanted = {{JsonSerializer.Serialize(journalNumber)}};
                var all = Array.prototype.slice.call(document.querySelectorAll('a'));
                var out = [];

                var rows = document.querySelectorAll('tr');
                for (var r = 0; r < rows.length; r++) {
                    var cells = rows[r].querySelectorAll('td');
                    if (cells.length < 3) continue;

                    // Match the issue by its number cell, not by row position.
                    var isRow = false;
                    for (var c = 0; c < Math.min(3, cells.length); c++) {
                        if ((cells[c].innerText || '').trim() === wanted) { isRow = true; break; }
                    }
                    if (!isRow) continue;

                    var anchors = rows[r].querySelectorAll('a');
                    for (var a = 0; a < anchors.length; a++) {
                        var label = (anchors[a].innerText || '').replace(/\s+/g, ' ').trim();
                        if (label.length === 0) continue;
                        out.push({ index: all.indexOf(anchors[a]), label: label });
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
            if (index >= 0) links.Add(new LinkInfo(index, label));
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

        Report($"Clicking \"{link.Label}\"...");

        var clickScript = $$"""
            (function () {
                var all = document.querySelectorAll('a');
                var el = all[{{link.Index}}];
                if (!el) return "no-element";
                el.click();
                return "clicked";
            })();
            """;

        var clickResult = await RunScriptAsync(clickScript);
        if (clickResult != "clicked")
            return new DownloadOutcome(link.Label, false, null, 0, "The link could not be found on the page.");

        var finished = await Task.WhenAny(_downloadDone.Task, Task.Delay(timeout));
        _pendingTargetPath = null;

        if (finished != _downloadDone.Task)
            return new DownloadOutcome(link.Label, false, null, 0,
                "No download started within the timeout - this link probably navigates rather than downloading.");

        var path = _downloadDone.Task.Result;
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

        return new DownloadOutcome(link.Label, true, path, info.Length, null);
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
        }
        catch
        {
            // Teardown of a hidden helper is never worth surfacing.
        }
    }
}
