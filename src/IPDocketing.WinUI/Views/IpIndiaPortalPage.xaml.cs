using System.Collections.ObjectModel;
using System.Text.Json;
using IPDocketing.Core.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace IPDocketing.WinUI.Views;

/// <summary>
/// Embeds the real IP India portals via WebView2 (the same engine as Edge -
/// this is a documented Windows control, not a scraping trick). The person
/// still solves the CAPTCHA by hand in the embedded page; everything this
/// page automates is either (a) typing data the person already gave the
/// app themselves (OTP, search terms, application numbers), or (b) reading
/// results out of a session the person has already unlocked with their own
/// CAPTCHA solve. Nothing here touches, solves, or works around the
/// CAPTCHA itself, and bulk fetch never re-solves it per item - it runs
/// inside the one session you already authenticated.
///
/// SELECTOR CAVEAT (applies to every ExecuteScriptAsync call below): these
/// are best-guess field/element selectors, not confirmed against the live
/// rendered DOM, since I only ever had this site's text-extracted content
/// to work from, never raw HTML with real element ids. The bulk-fetch
/// status extraction uses a sturdier technique than the others - matching
/// each field by its visible label text (e.g. "Status", "Proprietor")
/// rather than guessing an element id - based on a working pattern found
/// in Ritam-Guha/Trademark-scrapper, a 2019 Java/Selenium scraper against
/// IP India's older eRegister tool. That project's exact selectors don't
/// apply here (different site era), but the label-matching strategy and
/// its documented field list (Status, Application No, Class, Proprietor,
/// Agent/Attorney, Valid-upto, plus a documents table with name/date/link
/// per row) transfer even though the site doesn't. Open DevTools (F12) in
/// the embedded browser to confirm/adjust against the current page.
/// </summary>
public sealed partial class IpIndiaPortalPage : Page
{
    private const string TrademarkSearchUrl = "https://tmrsearch.ipindia.gov.in/tmrpublicsearch";
    private const string EStatusUrl = "https://tmrsearch.ipindia.gov.in/estatus";
    private const string PatentSearchUrl = "https://iprsearch.ipindia.gov.in/PublicSearch";

    /// <summary>
    /// Comprehensive e-Filing Services. This is where an agent's own filings
    /// live: sign in with your own credentials and your applications list is
    /// right there, which is the only lawful route to a bulk view of your
    /// portfolio - the public search has no agent-code facet at all.
    /// </summary>
    private const string CefsUrl = "https://ipindiaonline.gov.in/trademarkefiling/user/frmloginNew.aspx";

    private readonly ObservableCollection<FetchedStatusRow> _bulkResults = new();

    public IpIndiaPortalPage()
    {
        InitializeComponent();
        BulkResultsList.ItemsSource = _bulkResults;
        Loaded += async (_, _) => await InitBrowserAsync();
    }

    private async System.Threading.Tasks.Task InitBrowserAsync()
    {
        try
        {
            // The user-data folder is redirected in App.ConfigureWebView2Storage
            // via the WEBVIEW2_USER_DATA_FOLDER environment variable, which is
            // read by the WebView2 loader itself.
            //
            // Two attempts at doing this through CoreWebView2Environment.CreateAsync
            // failed to compile against this WebView2 build - first CS1739 on a
            // named argument, then CS1501 on the three-argument form. Rather
            // than guess a third overload shape, this uses the documented
            // environment variable, which has no overload to get wrong and is
            // stable across every WebView2 version.
            await Browser.EnsureCoreWebView2Async();

            // Catch downloads produced by clicking a link that has no URL.
            // Panel View links are often ASP.NET postbacks - there is nothing to
            // fetch, so the file has to be obtained by clicking and intercepting
            // what comes back, which is what a person does.
            Browser.CoreWebView2.DownloadStarting += OnPortalDownloadStarting;

            Browser.CoreWebView2.Navigate(TrademarkSearchUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't start the embedded browser: {ex.Message}. " +
                               "The WebView2 Runtime may need to be installed (it ships with Windows 10/11 by default).";
        }
    }

    /// <summary>
    /// Runs one of the PortalScripts against the page, substituting the JSON
    /// payload, and returns the parsed result. WebView2 returns the script's
    /// value as a JSON-encoded string, so the outer layer is unwrapped before
    /// the inner JSON is parsed - skipping that unwrap is why script results
    /// used to come back as unusable quoted blobs.
    /// </summary>
    private string? _pendingDownloadPath;
    private TaskCompletionSource<string?>? _pendingDownload;

    /// <summary>
    /// True while a long portal run is in progress. Guards every handler that
    /// can take minutes.
    ///
    /// WHY THIS IS NOT OPTIONAL
    ///
    /// These handlers are `async void`, so an exception inside one does not
    /// return to a caller - it goes to the dispatcher and terminates the
    /// process. And a second run is very easy to start: the first click shows no
    /// immediate feedback, so people click again. When it reaches
    /// `wait.ShowAsync()` while the first run's CAPTCHA dialog is still open,
    /// WinUI throws - only one ContentDialog may be open at a time - and the app
    /// closes with no message.
    ///
    /// The quieter failure is worse. Two overlapping runs share
    /// _pendingDownloadPath, _pendingDownload and _bulkResults, so run B's
    /// capture can return run A's file, and B then files A's document against
    /// B's matter, reporting success.
    /// </summary>
    private bool _runInProgress;

    /// <summary>
    /// Claims the run, releasing it when the scope is disposed.
    ///
    /// A disposable scope rather than a try/finally so that `using var` at the
    /// top of a handler is the whole of the change - the flag is released when
    /// the method completes, including down every early-return path and every
    /// exception path, and none of these long methods needed re-indenting to get
    /// that guarantee.
    /// </summary>
    private readonly struct RunScope : IDisposable
    {
        private readonly IpIndiaPortalPage? _page;

        internal RunScope(IpIndiaPortalPage? page) => _page = page;

        /// <summary>False when another run already holds the portal.</summary>
        public bool Started => _page is not null;

        public void Dispose()
        {
            if (_page is not null) _page._runInProgress = false;
        }
    }

    private RunScope BeginRun(string what)
    {
        if (_runInProgress)
        {
            StatusText.Text = $"{what} is already running - let it finish before starting another.";
            return new RunScope(null);
        }

        _runInProgress = true;
        return new RunScope(this);
    }

    private void OnPortalDownloadStarting(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            args.Handled = true; // no download popup

            if (_pendingDownloadPath is not null)
                args.ResultFilePath = _pendingDownloadPath;

            var operation = args.DownloadOperation;
            var path = args.ResultFilePath;

            void Settle()
            {
                switch (operation.State)
                {
                    case Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed:
                        _pendingDownload?.TrySetResult(path);
                        break;
                    case Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted:
                        _pendingDownload?.TrySetResult(null);
                        break;
                }
            }

            operation.StateChanged += (_, _) => Settle();

            // A cached or small file can finish before the handler attaches;
            // checking now closes that race.
            Settle();
        }
        catch
        {
            _pendingDownload?.TrySetResult(null);
        }
    }

    /// <summary>
    /// Obtains a document by clicking its link and catching the download.
    /// Used when the row carries no URL. Returns the saved path, or null.
    /// </summary>
    private async System.Threading.Tasks.Task<string?> ClickAndCaptureAsync(
        int linkIndex, string targetPath, TimeSpan timeout)
    {
        _pendingDownloadPath = targetPath;
        _pendingDownload = new TaskCompletionSource<string?>();

        try
        {
            var clicked = await RunScriptAsync(PortalScripts.ClickPanelLink, new { index = linkIndex });
            if (clicked is not { } c || !c.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return null;

            var finished = await System.Threading.Tasks.Task.WhenAny(
                _pendingDownload.Task, System.Threading.Tasks.Task.Delay(timeout));

            return finished == _pendingDownload.Task ? _pendingDownload.Task.Result : null;
        }
        finally
        {
            _pendingDownloadPath = null;
        }
    }

    private async System.Threading.Tasks.Task<JsonElement?> RunScriptAsync(string script, object? payload = null)
    {
        if (Browser.CoreWebView2 is null) return null;

        var json = payload is null ? "null" : JsonSerializer.Serialize(payload);
        var prepared = script.Replace(PortalScripts.PayloadToken, json);

        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(prepared);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;

            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(inner)) return null;

            return JsonDocument.Parse(inner).RootElement.Clone();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Portal script failed: {ex}");
            return null;
        }
    }

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        var list = new List<string>();
        if (element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.GetString() is { Length: > 0 } value) list.Add(value);
        return list;
    }

    private static string TagOf(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void PriorArtBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) FillPriorArtSearch_Click(sender, e);
    }

    private void GoToTrademark_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.Navigate(TrademarkSearchUrl);
    }

    private void GoToEStatus_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.Navigate(EStatusUrl);
    }

    private void GoToPatent_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.Navigate(PatentSearchUrl);
    }

    private void GoToCefs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        Browser.CoreWebView2.Navigate(CefsUrl);
        StatusText.Text = "Sign in with your own agent credentials, open your applications list, " +
                          "then press 'Import filings from page'. Nothing is read until you do.";
    }

    /// <summary>
    /// Imports the table currently displayed in the embedded browser.
    ///
    /// This reads what is already rendered in a session you signed into
    /// yourself - your own filings, your own account. It does not handle
    /// credentials, does not touch the CAPTCHA, and cannot reach anyone else's
    /// records.
    ///
    /// The column mapping is proposed and then confirmed by you rather than
    /// applied silently. Getting a column wrong here would write hundreds of
    /// records with, say, a registration date sitting in the filing date field,
    /// which then anchors every renewal term years off - and nothing on screen
    /// would say so. Everything goes through the same validation, day-first
    /// date parsing and preview as a CSV import; there is no second, weaker
    /// path.
    /// </summary>
    private async void ImportFromPage_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            StatusText.Text = "The embedded browser isn't ready yet.";
            return;
        }

        StatusText.Text = "Reading tables on the current page...";

        var result = await RunScriptAsync(PortalScripts.ExtractTables);
        if (result is not { } payload ||
            !payload.TryGetProperty("tables", out var tablesEl) ||
            tablesEl.GetArrayLength() == 0)
        {
            StatusText.Text = "No data table was found on this page. Navigate to a results or filings list " +
                              "that shows rows on screen, then try again.";
            return;
        }

        var tables = new List<PageTable>();
        foreach (var item in tablesEl.EnumerateArray())
        {
            var headers = new List<string>();
            if (item.TryGetProperty("headers", out var headersEl))
                foreach (var h in headersEl.EnumerateArray())
                    headers.Add(h.GetString() ?? "");

            var rows = new List<List<string>>();
            if (item.TryGetProperty("rows", out var rowsEl))
                foreach (var r in rowsEl.EnumerateArray())
                {
                    var cells = new List<string>();
                    foreach (var c in r.EnumerateArray()) cells.Add(c.GetString() ?? "");
                    rows.Add(cells);
                }

            var caption = item.TryGetProperty("caption", out var capEl) ? capEl.GetString() ?? "" : "";
            tables.Add(new PageTable(caption, headers, rows));
        }

        // Pick the table, if the page has more than one worth considering.
        var chosen = tables[0];
        if (tables.Count > 1)
        {
            var tablePicker = new ComboBox
            {
                Header = "Which table holds your filings?",
                ItemsSource = tables.Select((t, i) =>
                    $"{i + 1}. {t.RowCount} row(s), {t.Headers.Count} column(s)" +
                    (string.IsNullOrWhiteSpace(t.Caption) ? "" : $" — {t.Caption}") +
                    $"  [{string.Join(" | ", t.Headers.Take(4))}]").ToList(),
                SelectedIndex = 0,
                MinWidth = 560
            };

            var pickDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Several tables on this page",
                Content = tablePicker,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await pickDialog.ShowAsync() != ContentDialogResult.Primary) return;
            chosen = tables[Math.Max(0, tablePicker.SelectedIndex)];
        }

        // Column mapping, pre-filled with a guess.
        var mapper = new TableImportMapper();
        var guess = mapper.GuessMapping(chosen.Headers);
        var pickers = new List<ComboBox>();

        var mappingPanel = new StackPanel { Spacing = 8 };
        mappingPanel.Children.Add(new TextBlock
        {
            Text = $"{chosen.RowCount} row(s) found. Confirm what each column is — " +
                   "anything left as (ignore) is not imported.",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            Opacity = 0.75
        });

        for (var i = 0; i < chosen.Headers.Count; i++)
        {
            var sample = chosen.Rows.FirstOrDefault(r => i < r.Count && !string.IsNullOrWhiteSpace(r[i]));
            var sampleText = sample is null ? "no sample" : sample[i];
            if (sampleText.Length > 40) sampleText = sampleText[..40] + "...";

            var row = new Grid { ColumnSpacing = 10, Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(250) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(220) });

            var label = new StackPanel();
            label.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(chosen.Headers[i]) ? $"(column {i + 1})" : chosen.Headers[i],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
            });
            label.Children.Add(new TextBlock
            {
                Text = "e.g. " + sampleText,
                FontSize = 11,
                Opacity = 0.55,
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
            });
            row.Children.Add(label);

            var picker = new ComboBox
            {
                ItemsSource = TableImportMapper.Targets,
                SelectedItem = guess[i],
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch
            };
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            pickers.Add(picker);

            mappingPanel.Children.Add(row);
        }

        var mappingDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Map the columns",
            Content = new ScrollViewer { Content = mappingPanel, MaxHeight = 440, Width = 520 },
            PrimaryButtonText = "Preview import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await mappingDialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var mapping = pickers.Select(p => p.SelectedItem as string ?? TableImportMapper.Ignore).ToList();
            var csv = mapper.BuildCsv(mapping, chosen.Rows);

            // Same validator, same date handling, same preview as a file import.
            var report = App.PortfolioImport.Validate(csv);

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"{report.NewCount} new matter(s), {report.UpdateCount} existing would be updated.");
            summary.AppendLine();

            if (report.Issues.Count > 0)
            {
                foreach (var issue in report.Issues.Take(30))
                    summary.AppendLine($"Line {issue.LineNumber} [{issue.Column}]: {issue.Message}");
                if (report.Issues.Count > 30)
                    summary.AppendLine($"...and {report.Issues.Count - 30} more.");
            }
            else summary.AppendLine("No problems found.");

            summary.AppendLine();
            summary.AppendLine("Note: this imports only the rows currently displayed. If your filings list " +
                               "is paginated, set it to show all rows (or import each page in turn) — " +
                               "the page cannot be read beyond what it has rendered.");

            var confirmDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = report.HasFatalIssues ? "Import blocked" : "Preview import",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = summary.ToString(),
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        FontSize = 12
                    },
                    MaxHeight = 400,
                    Width = 520
                },
                PrimaryButtonText = report.HasFatalIssues ? string.Empty : "Import",
                CloseButtonText = report.HasFatalIssues ? "Close" : "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

            var (created, updated) = App.PortfolioImport.Import(report);
            var renewals = App.Renewals.DocketRenewals();

            StatusText.Text = $"Imported {created} new and updated {updated} matter(s) from this page. " +
                              $"{renewals.DeadlinesCreated} renewal deadline(s) docketed automatically.";

            App.Audit.Log("Import", "Matter", 0,
                $"Imported {created} new / {updated} updated matter(s) from an authenticated portal page.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    private sealed record PageTable(string Caption, List<string> Headers, List<List<string>> Rows)
    {
        public int RowCount => Rows.Count;
    }

    private async void AutoFillOtp_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!App.GmailOtp.IsConfigured)
        {
            StatusText.Text = "Gmail isn't connected yet. See the setup steps in GmailOtpService.cs " +
                               "(one-time Google Cloud OAuth client setup, then drop the JSON in the app data folder).";
            return;
        }

        StatusText.Text = "Looking for a recent OTP email...";
        string? otp;
        try
        {
            otp = await App.GmailOtp.FindRecentOtpAsync(TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Gmail lookup failed: {ex.Message}";
            return;
        }

        if (otp is null)
        {
            StatusText.Text = "No OTP email found in the last 5 minutes yet - request the OTP on the page below first, then try again in a few seconds.";
            return;
        }

        if (Browser.CoreWebView2 is null)
        {
            StatusText.Text = $"Found OTP {otp}, but the browser isn't ready to receive it.";
            return;
        }

        var otpResult = await RunScriptAsync(PortalScripts.FillOtp, new { otp });
        var otpFilled = otpResult is { } r && r.TryGetProperty("filled", out var f) && f.ValueKind == JsonValueKind.True;

        StatusText.Text = otpFilled
            ? $"OTP {otp} auto-filled. Check it on the page and press Verify yourself."
            : $"Found OTP {otp}, but no OTP field was visible on the current page. " +
              "Type it in manually, or press Diagnose to see what fields the page is exposing.";
    }

    private async void FillPriorArtSearch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            FillResultText.Text = "The embedded browser isn't ready yet.";
            return;
        }

        var mark = PriorArtNameBox.Text?.Trim() ?? "";
        var tmClass = PriorArtClassBox.Text?.Trim() ?? "";

        if (mark.Length == 0 && tmClass.Length == 0)
        {
            FillResultText.Text = "Enter a mark name or a class first.";
            return;
        }

        FillResultText.Text = "Filling the form...";

        var result = await RunScriptAsync(PortalScripts.FillTrademarkSearch, new
        {
            mark,
            tmClass,
            searchType = TagOf(SearchTypeBox, "wordmark"),
            criteria = TagOf(MatchCriteriaBox, "contains")
        });

        if (result is not { } payload)
        {
            FillResultText.Text = "The page didn't respond to the fill script. " +
                                  "Load the trademark search page first (button above), then try again.";
            return;
        }

        var filled = ReadStringArray(payload, "filled");
        var missing = ReadStringArray(payload, "missing");

        if (filled.Count == 0)
        {
            FillResultText.Text = "Nothing could be filled - none of the expected fields were found on this page. " +
                                  "Press Diagnose to see what the page actually exposes.";
            return;
        }

        var text = $"Filled: {string.Join(", ", filled)}.";
        if (missing.Count > 0) text += $" Not found: {string.Join(", ", missing)} — fill those by hand.";
        text += " Solve the CAPTCHA and press Search on the page.";
        FillResultText.Text = text;
    }

    /// <summary>
    /// Read-only inspection of the live page: lists every visible field with the
    /// label text it sits under. This exists because the failure mode it
    /// diagnoses is invisible otherwise - a field fills, the wrong one, and
    /// nothing on screen says so. Writes nothing and clicks nothing.
    /// </summary>
    private async void DiagnoseForm_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            FillResultText.Text = "The embedded browser isn't ready yet.";
            return;
        }

        var result = await RunScriptAsync(PortalScripts.DiagnoseForm);
        if (result is not { } payload)
        {
            FillResultText.Text = "Couldn't inspect the page.";
            return;
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine(payload.TryGetProperty("url", out var url) ? url.GetString() : "(unknown page)");
        report.AppendLine();

        void Section(string title, string property)
        {
            if (!payload.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
            report.AppendLine($"--- {title} ({array.GetArrayLength()}) ---");
            foreach (var item in array.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : "?";
                var id = item.TryGetProperty("id", out var i) ? i.GetString() : "";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : "";
                var value = item.TryGetProperty("value", out var v) ? v.GetString() : "";
                var label = item.TryGetProperty("label", out var l) ? l.GetString() : "";
                var isCaptcha = item.TryGetProperty("captcha", out var c) && c.ValueKind == JsonValueKind.True;

                report.AppendLine($"[{type}] id={id} name={name}");
                if (!string.IsNullOrWhiteSpace(value)) report.AppendLine($"    value: {value}");
                if (!string.IsNullOrWhiteSpace(label)) report.AppendLine($"    label: {label}");
                if (isCaptcha) report.AppendLine("    (treated as CAPTCHA - never written to)");
            }
            report.AppendLine();
        }

        Section("Text fields / selects", "fields");
        Section("Radios and checkboxes", "radios");
        Section("Buttons", "buttons");

        // A read-only TextBox with a fixed Height inside a ScrollViewer
        // collapses to roughly one visible line - the TextBox already hosts its
        // own scroll viewer, so nesting it gives an unconstrained measure. Every
        // diagnostic shown that way was effectively invisible. The shared dialog
        // uses a single ScrollViewer over a TextBlock, and writes the report to
        // disk as well so a rendering failure can never lose it again.
        await IPDocketing.WinUI.Services.TextReportDialog.ShowAsync(
            XamlRoot, "Fields on the current page", report.ToString(), "fielddiag");

        FillResultText.Text = "Field report shown, and saved to the Reports folder.";
    }

    private async void BulkFetch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        using var run = BeginRun("The bulk status fetch");
        if (!run.Started) return;

        var numbers = (BulkNumbersBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        if (numbers.Count == 0)
        {
            BulkStatusText.Text = "Paste at least one application number first.";
            return;
        }
        if (Browser.CoreWebView2 is null)
        {
            BulkStatusText.Text = "Browser isn't ready yet.";
            return;
        }

        _bulkResults.Clear();
        var alreadyLinkedAppNumbers = App.Matters.GetAll()
            .Where(m => !string.IsNullOrWhiteSpace(m.ApplicationNumber))
            .Select(m => m.ApplicationNumber!)
            .ToHashSet();

        for (int i = 0; i < numbers.Count; i++)
        {
            var number = numbers[i];
            BulkStatusText.Text = $"Fetching {i + 1} of {numbers.Count}: {number}...";

            try
            {
                var submitted = await RunScriptAsync(
                    PortalScripts.SubmitApplicationNumber, new { number });

                // BUG FIX: a null result (the script threw, or the browser was
                // between navigations) used to fall straight through to the
                // extraction below, which then read whatever page happened to be
                // showing and reported it as this number's status. A number that
                // was never actually submitted must not come back with an
                // answer. Null is now treated as failure, exactly like ok:false.
                // A null result (the script threw, or the browser was between
                // navigations) is a failure, not a silent success: a number that
                // was never submitted must not come back with an answer read off
                // whatever page happened to be showing.
                //
                // The script now also reports alreadyShowing, for the very common
                // case where the result for this number is already on screen. The
                // e-Register replaces the search form with the result, so there is
                // no number box to find at that point - which this used to report
                // as "open the e-Status page and sign in first" while the answer
                // sat in front of you.
                if (submitted is not { } sr ||
                    !sr.TryGetProperty("ok", out var ok) ||
                    ok.ValueKind != JsonValueKind.True)
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        "Couldn't find the application-number box, and the page isn't showing this number. " +
                        "Open the e-Register search, sign in, and try again.", false));
                    continue;
                }

                // The result loads by AJAX, so there is no navigation event to
                // await. Read, and read again while the page is still empty:
                // eight attempts a second apart is a real ceiling rather than a
                // single optimistic guess at how fast the Registry answers.
                //
                // This now runs PortalScripts.ReadStatusResult - the same reader
                // the guided run uses - instead of a second, divergent copy of
                // the extraction logic that lived here. That copy was the source
                // of the column bug: it looked for a cell whose text was the
                // label and returned the next cell in the SAME ROW, which is a
                // key/value assumption. The e-Register result is columnar, so
                // the cell after "Class" is the "Filing Mode" header, and the
                // class came back reading "Filing Mode". One reader, used
                // everywhere, cannot drift like that again.
                // Waits for the page to show THIS number, not merely to show
                // something. Polling on "is anything readable" broke out on the
                // previous application's result, which was still on screen while
                // the new one was still in flight - see ResultMatchesRequest.
                System.Text.Json.JsonElement? payload = null;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    payload = await RunScriptAsync(PortalScripts.ReadStatusResult);
                    if (ResultMatchesRequest(payload, number)) break;
                    await System.Threading.Tasks.Task.Delay(1000);
                }

                if (payload is not { } result || !ResultMatchesRequest(payload, number))
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        HasReadableResult(payload)
                            ? "The page is still showing a different application number, so nothing was " +
                              "recorded for this one. Give the portal a moment and run it again."
                            : "The page never showed a result for this number. If a CAPTCHA is waiting, " +
                              "solve it and press View, then run this again.", false));
                    continue;
                }

                var reading = ReadResult(result);

                _bulkResults.Add(new FetchedStatusRow(
                    number,
                    reading.Summary,
                    !alreadyLinkedAppNumbers.Contains(number)));

                // Write what was read back onto the matter, where one exists and
                // the box is ticked. Reading the register and then not recording
                // it is most of a job.
                if (ApplyStatusCheck.IsChecked == true)
                {
                    var matter = App.Matters.GetAll().FirstOrDefault(m =>
                        string.Equals(m.ApplicationNumber?.Trim(), number,
                            StringComparison.OrdinalIgnoreCase));

                    if (matter is not null && ApplyReadingToMatter(matter, reading))
                        BulkStatusText.Text = $"{number}: docket updated from the register.";
                }
            }
            catch (Exception ex)
            {
                _bulkResults.Add(new FetchedStatusRow(number, $"Fetch failed: {ex.Message}", false));
            }
        }

        BulkStatusText.Text = $"Done - {numbers.Count} number(s) fetched.";
    }

    /// <summary>
    /// Walks the e-Register flow you described, one application number at a
    /// time: click the Trade Mark Application/Registered Mark tab, select
    /// National/IRDI Number, type the number, then STOP and wait while you read
    /// the captcha and press View. Once the result is up it reads the status,
    /// opens both panels, and files every document.
    ///
    /// The pause is not a limitation I am working around - it is the design.
    /// The Registry put that captcha there to say bulk automated access is not
    /// on offer, and the honest maximum is to do every mechanical step for you
    /// and leave the one step that is deliberately human to you.
    ///
    /// Each navigation step is a separate script call with a wait between,
    /// because every transition is an ASP.NET postback: the National/IRDI radio
    /// does not exist in the DOM until the tab click has round-tripped, and the
    /// number box does not exist until the radio has.
    /// </summary>
    private async void GuidedEStatus_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        using var run = BeginRun("The guided e-Status run");
        if (!run.Started) return;

        var numbers = (BulkNumbersBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0).Distinct().ToList();

        if (numbers.Count == 0)
        {
            BulkStatusText.Text = "Paste at least one application number first.";
            return;
        }

        if (Browser.CoreWebView2 is null)
        {
            BulkStatusText.Text = "The embedded browser isn't ready yet.";
            return;
        }

        var matters = App.Matters.GetAll()
            .Where(m => !string.IsNullOrWhiteSpace(m.ApplicationNumber))
            .ToDictionary(m => m.ApplicationNumber!.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        _bulkResults.Clear();
        var filedTotal = 0;

        foreach (var number in numbers)
        {
            matters.TryGetValue(number, out var matter);

            BulkStatusText.Text = $"{number}: opening the search form...";

            var tab = await RunScriptAsync(PortalScripts.EStatusStep, new { step = "tab", value = "" });
            await System.Threading.Tasks.Task.Delay(1800);

            await RunScriptAsync(PortalScripts.EStatusStep, new { step = "national", value = "" });
            await System.Threading.Tasks.Task.Delay(1500);

            var typed = await RunScriptAsync(PortalScripts.EStatusStep, new { step = "number", value = number });
            var typedOk = typed is { } t && t.TryGetProperty("ok", out var tk) && tk.ValueKind == JsonValueKind.True;

            if (!typedOk)
            {
                _bulkResults.Add(new FetchedStatusRow(number,
                    "Couldn't find the Trade Mark/Application Number box. Sign in and land on the " +
                    "E-Register page first, then re-run.", false));
                continue;
            }

            // Hand back to the human for the captcha.
            var wait = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Captcha needed for {number}",
                Content = new TextBlock
                {
                    Text = $"{number} has been typed into the form.\n\n" +
                           "Read the captcha on the page, type it into 'Enter the captcha Code', " +
                           "and press View. Wait for the Matching Trade Marks result to appear, " +
                           "then press Continue here.\n\n" +
                           "Press Skip to move on without reading this one.",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                PrimaryButtonText = "Continue",
                SecondaryButtonText = "Skip",
                CloseButtonText = "Stop the run",
                DefaultButton = ContentDialogButton.Primary
            };

            var choice = await wait.ShowAsync();
            if (choice == ContentDialogResult.None) break;
            if (choice == ContentDialogResult.Secondary)
            {
                _bulkResults.Add(new FetchedStatusRow(number, "Skipped.", false));
                continue;
            }

            // --- read the status block ---
            // Everything below comes from one reading now. The mark name in
            // particular used to be found by scanning for a header containing
            // "Trade Mark" but not "No" - a test that also matches "Trade Mark
            // Type", so the mark could be named "WORD".
            var statusPayload = await RunScriptAsync(PortalScripts.ReadStatusResult);
            var reading = statusPayload is { } sp ? ReadResult(sp) : null;
            var markName = reading?.MarkName ?? "";

            // The checkbox is honoured here too. The other two callers both gate
            // this on ApplyStatusCheck; the guided run did not, so a user who
            // deliberately unticked "Also update matter status from the portal"
            // - usually because they do not trust today's reading - had the
            // status, class, proprietor, filing date and title written anyway.
            if (matter is not null && reading is not null && ApplyStatusCheck.IsChecked == true)
                ApplyReadingToMatter(matter, reading);

            // --- both document panels ---
            var filed = 0;
            var notes = new List<string>();

            if (matter is null)
            {
                notes.Add("No matching matter in the docket, so documents were not filed.");
            }
            else
            {
                var panelRun = await FileDocumentsFromPanelsAsync(matter, number);
                filed = panelRun.Filed;
                notes.AddRange(panelRun.Notes);
            }

            filedTotal += filed;

            var label = string.IsNullOrWhiteSpace(markName) ? number : $"{number} — {markName}";
            var summary = reading is null
                ? $"No result could be read. {filed} document(s) filed."
                : $"{reading.Summary}  —  {filed} document(s) filed.";
            if (notes.Count > 0) summary += " " + string.Join(" ", notes.Take(3));

            _bulkResults.Add(new FetchedStatusRow(label, summary, false));
        }

        BulkStatusText.Text = $"Run finished — {filedTotal} document(s) filed across {numbers.Count} number(s).";
    }

    /// <summary>
    /// Walks the application numbers in the bulk box, opens each one's status
    /// page inside the session you already unlocked, and files every document
    /// it lists against the matching matter.
    ///
    /// One CAPTCHA solve covers the whole run. That is the honest ceiling on
    /// automation here: the Registry gates the status page deliberately, so the
    /// app works inside a session you opened rather than around the gate. If
    /// the session lapses mid-run the loop stops and says so, rather than
    /// grinding on and filing login pages as if they were examination reports.
    ///
    /// Only numbers already on a matter are processed. Filing an examination
    /// report against nothing would leave an orphan document nobody finds
    /// again - use "Add to list" on a bulk-fetch result first.
    /// </summary>
    private async void FetchDocuments_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        using var run = BeginRun("The document fetch");
        if (!run.Started) return;

        var numbers = (BulkNumbersBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        if (numbers.Count == 0)
        {
            BulkStatusText.Text = "Paste at least one application number first.";
            return;
        }

        if (Browser.CoreWebView2 is null)
        {
            BulkStatusText.Text = "The embedded browser isn't ready yet.";
            return;
        }

        // Map numbers to matters up front, so an unmatched number is reported
        // once rather than after a pointless page load.
        var matters = App.Matters.GetAll()
            .Where(m => !string.IsNullOrWhiteSpace(m.ApplicationNumber))
            .ToDictionary(m => m.ApplicationNumber!.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        var unmatched = numbers.Where(n => !matters.ContainsKey(n)).ToList();
        var actionable = numbers.Where(matters.ContainsKey).ToList();

        if (actionable.Count == 0)
        {
            BulkStatusText.Text = $"None of those {numbers.Count} number(s) match a matter in the docket. " +
                                  "Run a bulk status fetch first and use 'Add to list', or import them.";
            return;
        }

        _bulkResults.Clear();
        var savedTotal = 0;
        var skippedTotal = 0;
        var sessionLost = false;

        for (var i = 0; i < actionable.Count && !sessionLost; i++)
        {
            var number = actionable[i];
            var matter = matters[number];
            BulkStatusText.Text = $"Opening {number} ({i + 1} of {actionable.Count})...";

            try
            {
                var submitted = await RunScriptAsync(
                    PortalScripts.SubmitApplicationNumber, new { number });

                // Null (the script threw, or the browser was mid-navigation) is
                // a failure, not a silent success - the same fix as in
                // BulkFetch_Click. Reading documents off a page that never
                // received this number would file another mark's papers against
                // this matter, which is far worse than reporting a miss.
                if (submitted is not { } sr ||
                    !sr.TryGetProperty("ok", out var ok) ||
                    ok.ValueKind != JsonValueKind.True)
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        "Couldn't find the application-number box, and the page isn't showing this number. " +
                        "Open the e-Register search, sign in, and try again.", false));
                    continue;
                }

                // The result loads by AJAX, so there is no navigation event to
                // await. Wait for the reader to actually see a result rather
                // than guessing at a fixed delay.
                // Same identity check as the fetch path. This one matters more,
                // because the documents filed below are filed against the matter
                // for `number` - reading a stale page here files one mark's
                // examination report against another mark's docket.
                System.Text.Json.JsonElement? statusPayload = null;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    statusPayload = await RunScriptAsync(PortalScripts.ReadStatusResult);
                    if (ResultMatchesRequest(statusPayload, number)) break;
                    await System.Threading.Tasks.Task.Delay(1000);
                }

                if (statusPayload is not { } shown || !ResultMatchesRequest(statusPayload, number))
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        HasReadableResult(statusPayload)
                            ? "The page is still showing a different application number, so nothing was " +
                              "read or filed for this one. Give the portal a moment and run it again."
                            : "The page never showed a result for this number - if a CAPTCHA is waiting, " +
                              "solve it and press View, then run this again.", false));
                    continue;
                }

                var reading = ReadResult(shown);

                // WHY THIS NO LONGER USES ExtractDocumentLinks.
                //
                // That script scans the page for anchors pointing at files. On
                // the e-Register result page there are none: the papers live
                // behind four buttons - PR Details, Reminders, Correspondence &
                // Notices, Uploaded Documents - each of which opens a modal, and
                // the View links inside those modals are ASP.NET postbacks with
                // no URL at all. So the scan found nothing and the run reported
                // "listed no downloadable documents", which read as "this mark
                // has no papers" when it meant "I looked in the wrong place".
                //
                // The guided run already walked those panels correctly. Both
                // paths now call the same method, so neither can drift from the
                // other again - which is exactly how this divergence started.
                var panelRun = await FileDocumentsFromPanelsAsync(matter, number);

                var saved = panelRun.Filed;
                var skipped = panelRun.Skipped;
                var notes = new List<string>(panelRun.Notes);
                sessionLost = panelRun.SessionLost;

                if (ApplyStatusCheck.IsChecked == true && ApplyReadingToMatter(matter, reading))
                    notes.Add($"Docket updated from the register (status '{reading.Status}').");

                savedTotal += saved;
                skippedTotal += skipped;

                var summary = $"{saved} document(s) filed, {skipped} skipped.";
                if (notes.Count > 0) summary += " " + string.Join(" ", notes.Take(4));

                var shownName = string.IsNullOrWhiteSpace(reading.MarkName) ? matter.Title : reading.MarkName;
                _bulkResults.Add(new FetchedStatusRow($"{number} — {shownName}", summary, false));
            }
            catch (Exception ex)
            {
                _bulkResults.Add(new FetchedStatusRow(number, $"Failed: {ex.Message}", false));
            }
        }

        var final = $"Done — {savedTotal} document(s) filed, {skippedTotal} skipped across {actionable.Count} number(s).";
        if (sessionLost)
            final = $"Stopped early: the portal session expired after {savedTotal} document(s). " +
                    "Sign in and solve the CAPTCHA again, then re-run — anything already filed is skipped.";
        if (unmatched.Count > 0)
            final += $" {unmatched.Count} number(s) had no matching matter and were ignored.";

        BulkStatusText.Text = final;
    }

    /// <summary>One canonical reading of an e-Register result page.</summary>
    private sealed record PortalReading(
        string? Status,
        string? SubStatus,
        string? AsOn,
        string? TrademarkNo,
        string? ApplicationDate,
        string? NiceClass,
        string? FilingMode,
        string? MarkName,
        string? TmType,
        string? Proprietor,
        string? ProprietorName,
        string? Publication,
        string? ValidUpto,
        List<string> Panels)
    {
        /// <summary>What the results list shows for this number.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                void Add(string label, string? value)
                {
                    if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{label}: {value}");
                }

                Add("Status", Status);
                if (!string.IsNullOrWhiteSpace(SubStatus) &&
                    !string.Equals(SubStatus, "Not Applicable", StringComparison.OrdinalIgnoreCase))
                    Add("Sub status", SubStatus);
                Add("Mark", MarkName);
                Add("Class", NiceClass);
                Add("Filed", ApplicationDate);
                Add("Type", TmType);
                Add("Filing mode", FilingMode);
                // NO FALLING BACK FROM Proprietor Name TO User Detail.
                //
                // This is the original "proprietor recorded as Proposed to be
                // used" bug, still alive in the display layer after the database
                // write was fixed. Proprietor is read from the User Detail
                // column - the use claim - and where the proprietor header is
                // spelled in a way `pick` does not match, ProprietorName comes
                // back empty and this printed "Proprietor: Proposed to be used".
                // CopyBulkReport then pastes that straight into a client email.
                // Better to show nothing than to show the use claim as the owner.
                Add("Proprietor", ProprietorName);
                Add("Use claim", Proprietor);
                Add("Valid upto", ValidUpto);
                Add("As on", AsOn);

                if (parts.Count == 0) return "The result was on screen but no fields could be read from it.";

                var summary = string.Join(" | ", parts);
                if (Panels.Count > 0) summary += $"  ({Panels.Count} document panel(s): {string.Join(", ", Panels)})";
                return summary;
            }
        }
    }

    private static string? Text(System.Text.Json.JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// True once the page is actually showing a result. Used to decide whether
    /// to keep waiting - a page still rendering the empty search form reads as
    /// "no fields", which is a reason to wait, not a reason to report a miss.
    /// </summary>
    private static bool HasReadableResult(System.Text.Json.JsonElement? payload)
    {
        if (payload is not { } root) return false;

        if (!string.IsNullOrWhiteSpace(Text(root, "status"))) return true;

        return root.TryGetProperty("named", out var named) &&
               named.ValueKind == JsonValueKind.Object &&
               (!string.IsNullOrWhiteSpace(Text(named, "trademarkNo")) ||
                !string.IsNullOrWhiteSpace(Text(named, "markName")));
    }

    /// <summary>
    /// True when the page on screen is showing the application that was asked
    /// for - not the previous one.
    ///
    /// NOTHING USED TO CHECK THIS, and it is the difference between a docket
    /// entry that is right and one that is wrong about the wrong mark.
    /// HasReadableResult goes true the instant ANY status text is on screen, and
    /// after submitting a new number the PREVIOUS result is still rendered until
    /// the round-trip lands. In a bulk run over 7050751 then 7050752, the first
    /// poll fires immediately, sees mark 1 still displayed, breaks out of the
    /// wait, and mark 1's class, proprietor and filing date are written onto
    /// matter 2 - and then mark 1's examination report is filed against matter 2
    /// as well. Both look completely successful.
    ///
    /// Comparing digits only, because the page renders the number with spaces
    /// and the odd stray character, and a run of digits is the part that
    /// identifies an application.
    /// </summary>
    private static bool ResultMatchesRequest(System.Text.Json.JsonElement? payload, string requested)
    {
        if (payload is not { } root) return false;

        var shown = root.TryGetProperty("named", out var named) && named.ValueKind == JsonValueKind.Object
            ? Text(named, "trademarkNo")
            : null;

        // If the reader could not find a number at all we cannot prove it is
        // stale, and refusing every such page would break the pages that only
        // ever render a status line. Fall back to the readability test.
        if (string.IsNullOrWhiteSpace(shown)) return HasReadableResult(payload);

        return DigitsOf(shown) == DigitsOf(requested);
    }

    private static string DigitsOf(string? value) =>
        new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    /// <summary>
    /// The real content type of a captured download, read from its leading
    /// bytes, or null if it is not a document at all.
    ///
    /// The bytes are the only trustworthy source here. The click-and-capture
    /// path has no HTTP response to consult, and it used to simply assert
    /// "application/pdf" over whatever arrived - wrong in two ways. An expired
    /// session yields an HTML login page, which was being filed against the
    /// matter as an examination report; and a great many genuine IP India
    /// attachments are TIFF rather than PDF, so every one of those was stored
    /// under a type it does not have.
    /// </summary>
    private static string? SniffContentType(byte[] bytes)
    {
        if (bytes.Length < 8) return null;

        bool Starts(params byte[] magic) =>
            bytes.Length >= magic.Length && !magic.Where((b, i) => bytes[i] != b).Any();

        if (Starts(0x25, 0x50, 0x44, 0x46)) return "application/pdf";      // %PDF
        if (Starts(0x49, 0x49, 0x2A, 0x00)) return "image/tiff";           // II*\0
        if (Starts(0x4D, 0x4D, 0x00, 0x2A)) return "image/tiff";           // MM\0*
        if (Starts(0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (Starts(0x50, 0x4B, 0x03, 0x04)) return "application/zip";      // also docx/xlsx
        if (Starts(0xD0, 0xCF, 0x11, 0xE0)) return "application/msword";   // legacy Office

        // Anything whose first bytes read as markup is a web page, whatever the
        // server chose to call it.
        var head = System.Text.Encoding.ASCII
            .GetString(bytes, 0, Math.Min(512, bytes.Length))
            .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');

        if (head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("<title>", StringComparison.OrdinalIgnoreCase))
            return null;

        // Unrecognised but not markup: let it through rather than lose a real
        // document to an unusual format.
        return "application/octet-stream";
    }

    /// <summary>
    /// Every valid Nice class in the register's class cell, comma-separated and
    /// de-duplicated, or an empty string if there is not one. Handles "9",
    /// "9, 42", "25 & 35" and "Class 9 and 42" alike.
    /// </summary>
    private static string NormaliseClasses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var classes = System.Text.RegularExpressions.Regex
            .Matches(value, @"\d{1,2}")
            .Select(m => int.Parse(m.Value))
            .Where(n => n is >= 1 and <= 45)
            .Distinct()
            .ToList();

        return classes.Count == 0 ? string.Empty : string.Join(", ", classes);
    }

    /// <summary>Turns the reader's JSON into the typed reading everything else uses.</summary>
    private static PortalReading ReadResult(System.Text.Json.JsonElement root)
    {
        var named = root.TryGetProperty("named", out var n) && n.ValueKind == JsonValueKind.Object
            ? n
            : default;

        var panels = new List<string>();
        if (root.TryGetProperty("panels", out var panelsEl) && panelsEl.ValueKind == JsonValueKind.Array)
            foreach (var panel in panelsEl.EnumerateArray())
                if (panel.GetString() is { Length: > 0 } label) panels.Add(label);

        string? FromNamed(string key) =>
            named.ValueKind == JsonValueKind.Object ? Text(named, key) : null;

        return new PortalReading(
            Text(root, "status"),
            Text(root, "subStatus"),
            Text(root, "asOn"),
            FromNamed("trademarkNo"),
            FromNamed("applicationDate"),
            FromNamed("niceClass"),
            FromNamed("filingMode"),
            FromNamed("markName"),
            FromNamed("tmType"),
            FromNamed("userDetail"),
            FromNamed("proprietorName"),
            FromNamed("publication"),
            FromNamed("validUpto"),
            panels);
    }

    /// <summary>
    /// Writes a reading onto a matter and returns whether anything changed.
    ///
    /// Empty fields are filled; fields that already hold something are LEFT
    /// ALONE. A register read is good evidence, but it is not better than what
    /// a person typed in deliberately, and silently overwriting a corrected
    /// proprietor name or a hand-set class with whatever the portal rendered
    /// today is how a docket stops being trusted. Status is the exception - it
    /// goes through DocumentIngest.ApplyStatus, which is the existing audited
    /// path for exactly this.
    /// </summary>
    private static bool ApplyReadingToMatter(
        IPDocketing.Core.Models.Matter matter, PortalReading reading)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(reading.Status) &&
            App.DocumentIngest.ApplyStatus(matter.Id, reading.Status, null))
            changed = true;

        // A MULTI-CLASS FILING USED TO LOSE ITS CLASS SILENTLY.
        //
        // The register renders a multi-class application as "9, 42" or "25 & 35".
        // int.TryParse fails on both, the whole condition went false, nothing was
        // written and NO note was produced anywhere - while the on-screen summary
        // still printed "Class: 9, 42", so the run looked like it had docketed
        // the class. Every class in the reading is now kept, in the register's
        // own order, and the value is only rejected if not one number in it is a
        // real Nice class.
        if (string.IsNullOrWhiteSpace(matter.NiceClass) &&
            NormaliseClasses(reading.NiceClass) is { Length: > 0 } classes)
        {
            matter.NiceClass = classes;
            changed = true;
        }

        // The register shows the owner in "Proprietor Name" and the use claim in
        // "User Detail". Writing the latter into ProprietorName gave matters a
        // proprietor of "Proposed to be used".
        if (string.IsNullOrWhiteSpace(matter.ProprietorName) &&
            !string.IsNullOrWhiteSpace(reading.ProprietorName))
        {
            matter.ProprietorName = reading.ProprietorName;
            changed = true;
        }

        if (matter.FilingDate is null && ParsePortalDate(reading.ApplicationDate) is { } filed)
        {
            matter.FilingDate = filed;
            changed = true;
        }

        // The title is replaced only where it is still the placeholder written
        // by "Add to list", never where someone has named the matter themselves.
        if (!string.IsNullOrWhiteSpace(reading.MarkName) &&
            (string.IsNullOrWhiteSpace(matter.Title) ||
             matter.Title.StartsWith("Imported from e-Status", StringComparison.OrdinalIgnoreCase)))
        {
            matter.Title = reading.MarkName;
            changed = true;
        }

        if (changed)
        {
            try { App.Database.SaveChanges(); }
            catch { return false; }
        }

        return changed;
    }

    /// <summary>Outcome of walking the result page's document panels.</summary>
    private sealed record PanelFileResult(int Filed, int Skipped, bool SessionLost, List<string> Notes);

    /// <summary>
    /// Opens every document panel on the current result and files what it finds
    /// against <paramref name="matter"/>.
    ///
    /// THE ONE COPY. Bulk document fetch used to scan the page for anchors
    /// pointing at files (PortalScripts.ExtractDocumentLinks) while the guided
    /// run walked these panels. The e-Register has no such anchors - the papers
    /// sit behind four modal buttons whose View links are postbacks with no URL
    /// - so the bulk path found nothing and said the mark had no documents.
    /// Both callers now share this method.
    ///
    /// All four panels are visited, not two. "PR Details" and "Reminders" were
    /// unreachable before, so anything filed under them was invisible to the app.
    /// </summary>
    private async System.Threading.Tasks.Task<PanelFileResult> FileDocumentsFromPanelsAsync(
        IPDocketing.Core.Models.Matter matter, string number)
    {
        var filed = 0;
        var skipped = 0;
        var sessionLost = false;
        var notes = new List<string>();

        foreach (var panel in new[] { "documents", "correspondence", "prdetails", "reminders" })
        {
            if (sessionLost) break;

            BulkStatusText.Text = $"{number}: opening {panel}...";

            var opened = await RunScriptAsync(PortalScripts.OpenResultPanel, new { panel });
            if (opened is not { } op || !op.TryGetProperty("opened", out var wasOpen) ||
                wasOpen.ValueKind != JsonValueKind.True)
            {
                // Not every mark has every panel, so this is information rather
                // than an error - but it is still worth saying, because "this
                // panel isn't on the page" and "this panel was empty" are
                // different facts about the mark.
                notes.Add($"{panel}: no such panel on this result.");
                continue;
            }

            await System.Threading.Tasks.Task.Delay(1400);

            var rowsPayload = await RunScriptAsync(PortalScripts.ReadOpenPanelRows);
            if (rowsPayload is { } rp && rp.TryGetProperty("rows", out var rows) &&
                rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var url = Text(row, "url");
                    var description = Text(row, "description") ?? "Document";
                    var dateText = Text(row, "date");

                    // Two examination reports can be issued on the same day -
                    // your own record shows 23189976 and 23189978, both dated
                    // 09/07/2026, both titled "EXAMINATION REPORT". Filed under
                    // description and date alone they are indistinguishable, and
                    // the second looks like a duplicate of the first. The
                    // correspondence and despatch numbers are what tell them
                    // apart, so they go into the description.
                    var corresNo = Text(row, "corresNo");
                    var despatchNo = Text(row, "despatchNo");
                    var despatchDate = Text(row, "despatchDate");

                    var qualifiers = new List<string>();
                    if (!string.IsNullOrWhiteSpace(corresNo)) qualifiers.Add($"Corres. {corresNo}");
                    if (!string.IsNullOrWhiteSpace(despatchNo)) qualifiers.Add($"Despatch {despatchNo}");
                    if (!string.IsNullOrWhiteSpace(despatchDate)) qualifiers.Add($"despatched {despatchDate}");
                    if (qualifiers.Count > 0) description += $" [{string.Join(", ", qualifiers)}]";
                    var linkIndex = row.TryGetProperty("linkIndex", out var li) &&
                                    li.ValueKind == JsonValueKind.Number ? li.GetInt32() : -1;

                    byte[]? content = null;
                    string? contentType = null;

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var fetched = await RunScriptAsync(PortalScripts.FetchFileAsBase64, new { url });
                        if (fetched is { } fpay &&
                            fpay.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                        {
                            var base64 = Text(fpay, "data");
                            if (!string.IsNullOrWhiteSpace(base64))
                            {
                                content = Convert.FromBase64String(base64);
                                contentType = Text(fpay, "contentType");
                            }
                        }
                        else
                        {
                            var reason = fetched is { } fr ? Text(fr, "reason") ?? "unknown" : "unknown";
                            if (reason == "session-expired")
                            {
                                sessionLost = true;
                                notes.Add("Session expired.");
                                break;
                            }
                            notes.Add($"{description}: {reason}");
                        }
                    }

                    // No URL - the View link is a postback, which is the normal
                    // case here. Click it and catch the download, as a person
                    // would. Rows like this used to be dropped for having an
                    // empty url, which is most of why documents never arrived.
                    if (content is null && linkIndex >= 0)
                    {
                        var temp = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), $"ipd_{Guid.NewGuid():N}.pdf");

                        var captured = await ClickAndCaptureAsync(linkIndex, temp, TimeSpan.FromMinutes(2));

                        if (captured is not null && System.IO.File.Exists(captured))
                        {
                            try
                            {
                                var bytes = await System.IO.File.ReadAllBytesAsync(captured);

                                // NOTHING used to be checked here, and this is
                                // the NORMAL path on this site - the fetch
                                // branch above has an explicit session check and
                                // a size floor, and this one had neither. When
                                // the session lapsed mid-run, clicking View
                                // returned the login page, and those bytes were
                                // read, labelled application/pdf and filed
                                // against the matter as "EXAMINATION REPORT".
                                // The user finds out months later, when the PDF
                                // will not open.
                                var sniffed = SniffContentType(bytes);

                                if (sniffed is null)
                                {
                                    // Looks like a web page rather than a
                                    // document: the session is the usual reason.
                                    sessionLost = true;
                                    notes.Add($"{description}: the portal returned a web page instead of the " +
                                              "document, which normally means the session expired. Nothing was filed.");
                                    break;
                                }

                                content = bytes;
                                contentType = sniffed;
                            }
                            finally
                            {
                                try { System.IO.File.Delete(captured); } catch { }
                            }
                        }
                        else
                        {
                            notes.Add($"{description}: no URL, and clicking produced no download.");
                        }
                    }

                    if (content is null) { skipped++; continue; }

                    var result = App.DocumentIngest.Ingest(
                        matter.Id, content, description, panel, contentType, ParsePortalDate(dateText));

                    if (result.Saved)
                    {
                        filed++;

                        // OCR the freshly filed document so its text is
                        // searchable. Deliberately after the file is safely on
                        // disk - a failure here loses text, not the document.
                        if (result.DocumentId is { } docId)
                            _ = App.DocumentIngest.ExtractTextAsync(docId);
                    }
                    else
                    {
                        skipped++;
                        notes.Add($"{description}: {result.Reason}");
                    }
                }
            }

            // Close the modal so the next panel button is clickable again - and
            // CHECK THAT IT CLOSED. The script returns `closed`, and that result
            // used to be thrown away. When a modal fails to close, the next
            // panel's button is behind the overlay: click() does not throw, so
            // the open step reports success, the reader then re-reads the STILL
            // OPEN previous panel, and every document in it is downloaded and
            // ingested a second time under the next panel's category. The same
            // paper, filed twice against one matter, under two different
            // headings, with nothing to indicate it.
            var closeResult = await RunScriptAsync(PortalScripts.OpenResultPanel, new { panel = "close" });
            await System.Threading.Tasks.Task.Delay(600);

            var closed = closeResult is { } cr &&
                         cr.TryGetProperty("closed", out var closedEl) &&
                         closedEl.ValueKind == JsonValueKind.True;

            if (!closed)
            {
                // One retry, then stop rather than risk re-filing.
                await RunScriptAsync(PortalScripts.OpenResultPanel, new { panel = "close" });
                await System.Threading.Tasks.Task.Delay(800);

                var retry = await RunScriptAsync(PortalScripts.OpenResultPanel, new { panel = "close" });
                var reallyClosed = retry is { } rr &&
                                   rr.TryGetProperty("closed", out var rc) &&
                                   rc.ValueKind == JsonValueKind.True;

                if (!reallyClosed)
                {
                    notes.Add($"The {panel} panel would not close, so the remaining panels were skipped - " +
                              "reading them with it still open would have filed its documents again under " +
                              "another category.");
                    break;
                }
            }
        }

        return new PanelFileResult(filed, skipped, sessionLost, notes);
    }

    /// <summary>Day-first, as the portal writes them.</summary>
    private static DateTime? ParsePortalDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // BUG FIX: every format missing from the old five-entry list returned
        // null, and a null date files the document with no date at all rather
        // than reporting a problem. The portal is not consistent: document
        // tables print "12/08/2026", the register print-out uses "12-Aug-2026",
        // and some panels append a time.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text.Replace('\u00A0', ' '), @"\s+", " ").Trim();

        var embedded = System.Text.RegularExpressions.Regex.Match(cleaned,
            @"\b(\d{1,2}[\/\-\.\s][A-Za-z0-9]{1,9}[\/\-\.\s]\d{2,4})\b");
        if (embedded.Success) cleaned = embedded.Groups[1].Value.Trim();

        string[] formats =
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yy", "d/M/yy",
            "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "d-MMM-yyyy",
            "dd MMMM yyyy", "d MMMM yyyy", "yyyy-MM-dd",
        };

        foreach (var format in formats)
            if (DateTime.TryParseExact(cleaned, format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed.Year < 1900 ? null : parsed.Date;

        return null;
    }

    private async void CopyBulkReport_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_bulkResults.Count == 0)
        {
            BulkStatusText.Text = "Nothing fetched yet to copy.";
            return;
        }

        var report = string.Join(Environment.NewLine + Environment.NewLine,
            _bulkResults.Select(r => $"{r.ApplicationNumber}{Environment.NewLine}{r.StatusSummary}"));

        var package = new DataPackage();
        package.SetText(report);
        Clipboard.SetContent(package);
        BulkStatusText.Text = "Report copied - paste it into an email.";
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void AddFetchedToList_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FetchedStatusRow row }) return;

        // BUG FIX: pressing this twice - or pressing it for a number that was
        // already imported in an earlier run - created a second matter with the
        // same application number and a colliding "FETCHED-..." matter number.
        // The docket then had two records for one mark, and a later status
        // update landed on whichever one happened to be found first.
        var already = App.Matters.GetAll().FirstOrDefault(m =>
            string.Equals(m.ApplicationNumber?.Trim(), row.ApplicationNumber.Trim(),
                StringComparison.OrdinalIgnoreCase));

        if (already is not null)
        {
            row.NotAdded = false;
            BulkStatusText.Text = $"{row.ApplicationNumber} is already on the docket as {already.MatterNumber}.";
            return;
        }

        App.Matters.Add(new IPDocketing.Core.Models.Matter
        {
            MatterNumber = $"FETCHED-{row.ApplicationNumber}",
            ApplicationNumber = row.ApplicationNumber,
            Title = $"Imported from e-Status ({row.ApplicationNumber})",
            Type = IPDocketing.Core.Models.MatterType.Trademark,
            Country = "IN"
        });

        row.NotAdded = false;
        BulkStatusText.Text = $"{row.ApplicationNumber} added to Matters - open it to fill in the mark name, client, etc.";
    }

    public sealed class FetchedStatusRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string ApplicationNumber { get; }
        public string StatusSummary { get; }

        private bool _notAdded;
        public bool NotAdded
        {
            get => _notAdded;
            set { _notAdded = value; PropertyChanged?.Invoke(this, new(nameof(NotAdded))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public FetchedStatusRow(string applicationNumber, string statusSummary, bool notAdded)
        {
            ApplicationNumber = applicationNumber;
            StatusSummary = statusSummary;
            _notAdded = notAdded;
        }
    }
}
