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

        var box = new TextBox
        {
            Text = report.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Height = 420
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Fields on the current page",
            Content = new ScrollViewer { Content = box, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(report.ToString());
            Clipboard.SetContent(package);
            FillResultText.Text = "Field report copied to the clipboard.";
        }
    }

    private async void BulkFetch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
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

                if (submitted is { } sr && sr.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        "No application-number field was visible - load the e-Status page and sign in first.", false));
                    continue;
                }

                // The e-status result likely loads via AJAX rather than a full
                // page navigation, so there's no reliable "done" event to await -
                // this fixed delay is a heuristic, not a guarantee. Tune it once
                // the real page's timing is known.
                await System.Threading.Tasks.Task.Delay(2500);

                // SELECTOR TODO: table structure is still a guess for the
                // current site - but the STRATEGY here (match each field by
                // its visible label text, e.g. "Status", "Proprietor Name",
                // rather than guessing an element id) is a validated pattern
                // from Ritam-Guha/Trademark-scrapper, a 2019 Java scraper
                // against IP India's older eRegister tool. Label text tends
                // to survive a site redesign in a way internal ids don't,
                // so this should need less retuning than an id-based guess
                // even though the exact table layout has surely changed.
                // Also attempts to find a document-download table using the
                // same repo's pattern (a table of rows, each with a name,
                // date, and a download link) - this is the piece that
                // answers "fetch the examination report and other
                // documents", which wasn't buildable without this reference.
                var extracted = await Browser.CoreWebView2.ExecuteScriptAsync("""
                    (function() {
                        function findByLabel(label) {
                            var xpath = "//tr[contains(., '" + label + "')]";
                            var row = document.evaluate(xpath, document, null,
                                XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
                            if (!row) return '';
                            var cells = row.querySelectorAll('td');
                            return cells.length > 1 ? cells[cells.length - 1].innerText.trim() : '';
                        }

                        var fields = {
                            status: findByLabel('Status'),
                            applicationNo: findByLabel('Application No'),
                            tmClass: findByLabel('Class'),
                            proprietor: findByLabel('Proprietor'),
                            agent: findByLabel('Agent') || findByLabel('Attorney'),
                            validUpto: findByLabel('Valid') || findByLabel('Renewed')
                        };

                        // Look for a documents table: any table where most rows
                        // end in an <a href> (name/date/link per row, matching
                        // the reference repo's uploaded-documents pattern).
                        var docLinks = [];
                        document.querySelectorAll('table').forEach(function(table) {
                            var anchors = table.querySelectorAll('a[href]');
                            if (anchors.length >= 1 && anchors.length <= 20) {
                                anchors.forEach(function(a) {
                                    if (a.href && a.innerText.trim().length > 0) {
                                        docLinks.push(a.innerText.trim() + '|' + a.href);
                                    }
                                });
                            }
                        });

                        return JSON.stringify({ fields: fields, docs: docLinks.slice(0, 10) });
                    })();
                    """);

                var parsed = System.Text.Json.JsonDocument.Parse(
                    System.Text.Json.JsonSerializer.Deserialize<string>(extracted) ?? "{}");

                var root = parsed.RootElement;
                var statusText = "";
                if (root.TryGetProperty("fields", out var fieldsEl))
                {
                    var parts = new List<string>();
                    foreach (var prop in fieldsEl.EnumerateObject())
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) parts.Add($"{prop.Name}: {val}");
                    }
                    statusText = string.Join(" | ", parts);
                }

                var docCount = root.TryGetProperty("docs", out var docsEl) ? docsEl.GetArrayLength() : 0;
                if (docCount > 0) statusText += $" ({docCount} document link(s) found)";

                _bulkResults.Add(new FetchedStatusRow(
                    number,
                    string.IsNullOrWhiteSpace(statusText) ? "Couldn't read any fields - selectors need adjusting for the current page." : statusText,
                    !alreadyLinkedAppNumbers.Contains(number)));
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
            var statusPayload = await RunScriptAsync(PortalScripts.ReadStatusResult);
            var status = statusPayload is { } sp && sp.TryGetProperty("status", out var st)
                ? st.GetString()
                : null;

            var markName = "";
            if (statusPayload is { } fp && fp.TryGetProperty("fields", out var fields) &&
                fields.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in fields.EnumerateObject())
                    if (field.Name.Contains("Trade Mark", StringComparison.OrdinalIgnoreCase) &&
                        !field.Name.Contains("No", StringComparison.OrdinalIgnoreCase))
                        markName = field.Value.GetString() ?? "";
            }

            if (matter is not null && !string.IsNullOrWhiteSpace(status))
                App.DocumentIngest.ApplyStatus(matter.Id, status, null);

            // --- both document panels ---
            var filed = 0;
            var notes = new List<string>();

            if (matter is null)
            {
                notes.Add("No matching matter in the docket, so documents were not filed.");
            }
            else
            {
                foreach (var panel in new[] { "documents", "correspondence" })
                {
                    BulkStatusText.Text = $"{number}: opening {panel}...";

                    var opened = await RunScriptAsync(PortalScripts.OpenResultPanel, new { panel });
                    if (opened is not { } op || !op.TryGetProperty("opened", out var wasOpen) ||
                        wasOpen.ValueKind != JsonValueKind.True)
                    {
                        notes.Add($"{panel}: panel button not found.");
                        continue;
                    }

                    await System.Threading.Tasks.Task.Delay(1400);

                    var rowsPayload = await RunScriptAsync(PortalScripts.ReadOpenPanelRows);
                    if (rowsPayload is not { } rp || !rp.TryGetProperty("rows", out var rows)) continue;

                    foreach (var row in rows.EnumerateArray())
                    {
                        var url = row.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;

                        var description = row.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var dateText = row.TryGetProperty("date", out var dt) ? dt.GetString() : null;

                        var fetched = await RunScriptAsync(PortalScripts.FetchFileAsBase64, new { url });
                        if (fetched is not { } fpay ||
                            !fpay.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                        {
                            var reason = fetched is { } fr && fr.TryGetProperty("reason", out var rr)
                                ? rr.GetString() : "unknown";
                            notes.Add($"{description}: {reason}");
                            continue;
                        }

                        var base64 = fpay.TryGetProperty("data", out var dataEl) ? dataEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(base64)) continue;

                        var contentType = fpay.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;

                        var result = App.DocumentIngest.Ingest(
                            matter.Id, Convert.FromBase64String(base64),
                            description, panel, contentType, ParsePortalDate(dateText));

                        if (result.Saved) filed++;
                        else notes.Add($"{description}: {result.Reason}");
                    }

                    // Close the modal so the next panel button is clickable.
                    await RunScriptAsync(PortalScripts.OpenResultPanel, new { panel = "close" });
                    await System.Threading.Tasks.Task.Delay(600);
                }
            }

            filedTotal += filed;

            var label = string.IsNullOrWhiteSpace(markName) ? number : $"{number} — {markName}";
            var summary = $"Status: {status ?? "not read"}. {filed} document(s) filed.";
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

                if (submitted is { } sr && sr.TryGetProperty("ok", out var ok) &&
                    ok.ValueKind == JsonValueKind.False)
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        "No application-number field on this page - open the e-Status page and sign in first.", false));
                    continue;
                }

                // The result loads by AJAX, so there is no navigation event to
                // await. This delay is a heuristic, not a guarantee.
                await System.Threading.Tasks.Task.Delay(2500);

                var listed = await RunScriptAsync(PortalScripts.ExtractDocumentLinks);
                if (listed is not { } docPayload ||
                    !docPayload.TryGetProperty("documents", out var docsEl) ||
                    docsEl.GetArrayLength() == 0)
                {
                    _bulkResults.Add(new FetchedStatusRow(number,
                        "Status page loaded but listed no downloadable documents.", false));
                    continue;
                }

                var saved = 0;
                var skipped = 0;
                var notes = new List<string>();

                foreach (var docEl in docsEl.EnumerateArray())
                {
                    var url = docEl.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    var label = docEl.TryGetProperty("label", out var l) ? l.GetString() ?? "Document" : "Document";
                    var context = docEl.TryGetProperty("context", out var c) ? c.GetString() : null;
                    var dateText = docEl.TryGetProperty("date", out var d) ? d.GetString() : null;

                    BulkStatusText.Text = $"{number}: fetching '{label}'...";

                    var fetched = await RunScriptAsync(PortalScripts.FetchFileAsBase64, new { url });
                    if (fetched is not { } filePayload) { skipped++; continue; }

                    if (!filePayload.TryGetProperty("ok", out var fileOk) || fileOk.ValueKind != JsonValueKind.True)
                    {
                        var reason = filePayload.TryGetProperty("reason", out var r) ? r.GetString() : "unknown";
                        if (reason == "session-expired")
                        {
                            sessionLost = true;
                            notes.Add("Session expired.");
                            break;
                        }
                        skipped++;
                        notes.Add($"{label}: {reason}");
                        continue;
                    }

                    var base64 = filePayload.TryGetProperty("data", out var dataEl) ? dataEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(base64)) { skipped++; continue; }

                    var contentType = filePayload.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;

                    var result = App.DocumentIngest.Ingest(
                        matter.Id, Convert.FromBase64String(base64),
                        label, context, contentType, ParsePortalDate(dateText));

                    if (result.Saved) saved++;
                    else { skipped++; notes.Add($"{label}: {result.Reason}"); }
                }

                // Status, where the page exposed it and you asked for it.
                if (!sessionLost && ApplyStatusCheck.IsChecked == true)
                {
                    var status = await ReadStatusFieldAsync();
                    if (status is not null && App.DocumentIngest.ApplyStatus(matter.Id, status, null))
                        notes.Add($"Status updated to '{status}'.");
                }

                savedTotal += saved;
                skippedTotal += skipped;

                var summary = $"{saved} document(s) filed, {skipped} skipped.";
                if (notes.Count > 0) summary += " " + string.Join(" ", notes.Take(4));

                _bulkResults.Add(new FetchedStatusRow($"{number} — {matter.Title}", summary, false));
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

    /// <summary>
    /// Reads the status line off the currently displayed result, reusing the
    /// label-matching approach rather than a fixed selector.
    /// </summary>
    private async System.Threading.Tasks.Task<string?> ReadStatusFieldAsync()
    {
        var result = await RunScriptAsync(PortalScripts.ExtractTables);
        if (result is not { } payload ||
            !payload.TryGetProperty("tables", out var tables)) return null;

        foreach (var table in tables.EnumerateArray())
        {
            if (!table.TryGetProperty("headers", out var headers) ||
                !table.TryGetProperty("rows", out var rows)) continue;

            var headerList = headers.EnumerateArray().Select(h => h.GetString() ?? "").ToList();
            var statusIndex = headerList.FindIndex(h => h.Contains("status", StringComparison.OrdinalIgnoreCase));
            if (statusIndex < 0) continue;

            foreach (var row in rows.EnumerateArray())
            {
                var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToList();
                if (statusIndex < cells.Count && !string.IsNullOrWhiteSpace(cells[statusIndex]))
                    return cells[statusIndex].Trim();
            }
        }

        return null;
    }

    /// <summary>Day-first, as the portal writes them.</summary>
    private static DateTime? ParsePortalDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy" };
        foreach (var format in formats)
            if (DateTime.TryParseExact(text.Trim(), format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed.Date;
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
