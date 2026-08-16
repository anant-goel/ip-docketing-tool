using System.Collections.ObjectModel;
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
/// to work from, never raw HTML with real element ids. Open DevTools (F12)
/// in the embedded browser, find the real ids, and update the selectors -
/// each spot is marked "SELECTOR TODO".
/// </summary>
public sealed partial class IpIndiaPortalPage : Page
{
    private const string TrademarkSearchUrl = "https://tmrsearch.ipindia.gov.in/tmrpublicsearch";
    private const string EStatusUrl = "https://tmrsearch.ipindia.gov.in/estatus";
    private const string PatentSearchUrl = "https://iprsearch.ipindia.gov.in/PublicSearch";

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
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Navigate(TrademarkSearchUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't start the embedded browser: {ex.Message}. " +
                               "The WebView2 Runtime may need to be installed (it ships with Windows 10/11 by default).";
        }
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

        // SELECTOR TODO: guess, not confirmed against the live page.
        var result = await Browser.CoreWebView2.ExecuteScriptAsync($$"""
            (function() {
                var field = document.querySelector('input[id*="otp" i]');
                if (!field) return 'not-found';
                field.value = '{{otp}}';
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
                return 'filled';
            })();
            """);

        StatusText.Text = result.Trim('"') == "filled"
            ? $"OTP {otp} auto-filled. Review it on the page and click Verify yourself."
            : $"Found OTP {otp}, but couldn't locate the field automatically - the selector needs adjusting (see code comment). You can type it in manually: {otp}";
    }

    private async void FillPriorArtSearch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        var name = PriorArtNameBox.Text?.Trim() ?? "";
        var tmClass = PriorArtClassBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            StatusText.Text = "Enter a mark name to search first.";
            return;
        }

        // SELECTOR TODO: guessed field ids for the search form.
        var result = await Browser.CoreWebView2.ExecuteScriptAsync($$"""
            (function() {
                var nameField = document.querySelector('input[id*="mark" i], input[id*="wordmark" i], input[name*="mark" i]');
                var classField = document.querySelector('input[id*="class" i], select[id*="class" i]');
                var filled = [];
                if (nameField) {
                    nameField.value = '{{name.Replace("'", "\\'")}}';
                    nameField.dispatchEvent(new Event('input', { bubbles: true }));
                    filled.push('name');
                }
                if (classField && '{{tmClass}}'.length > 0) {
                    classField.value = '{{tmClass}}';
                    classField.dispatchEvent(new Event('input', { bubbles: true }));
                    classField.dispatchEvent(new Event('change', { bubbles: true }));
                    filled.push('class');
                }
                return filled.join(',');
            })();
            """);

        var filledFields = result.Trim('"');
        StatusText.Text = string.IsNullOrEmpty(filledFields)
            ? "Couldn't locate the search form fields automatically - the selector needs adjusting (see code comment)."
            : $"Filled: {filledFields}. Review and hit search yourself on the page.";
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
                // SELECTOR TODO: guessed application-number field + submit button ids.
                await Browser.CoreWebView2.ExecuteScriptAsync($$"""
                    (function() {
                        var field = document.querySelector('input[id*="appno" i], input[id*="application" i], input[name*="appno" i]');
                        var button = document.querySelector('button[id*="search" i], input[type="submit"]');
                        if (!field) return 'no-field';
                        field.value = '{{number}}';
                        field.dispatchEvent(new Event('input', { bubbles: true }));
                        if (button) button.click();
                        return 'submitted';
                    })();
                    """);

                // The e-status result likely loads via AJAX rather than a full
                // page navigation, so there's no reliable "done" event to await -
                // this fixed delay is a heuristic, not a guarantee. Tune it once
                // the real page's timing is known.
                await System.Threading.Tasks.Task.Delay(2500);

                // SELECTOR TODO: guessed result-container selector.
                var statusResult = await Browser.CoreWebView2.ExecuteScriptAsync("""
                    (function() {
                        var el = document.querySelector('#statusResult, .status-result, [id*="result" i]');
                        return el ? el.innerText.trim().replace(/\s+/g, ' ').substring(0, 300) : '';
                    })();
                    """);

                var statusText = System.Text.Json.JsonSerializer.Deserialize<string>(statusResult) ?? "";
                _bulkResults.Add(new FetchedStatusRow(
                    number,
                    string.IsNullOrWhiteSpace(statusText) ? "Couldn't read status text - selector needs adjusting." : statusText,
                    !alreadyLinkedAppNumbers.Contains(number)));
            }
            catch (Exception ex)
            {
                _bulkResults.Add(new FetchedStatusRow(number, $"Fetch failed: {ex.Message}", false));
            }
        }

        BulkStatusText.Text = $"Done - {numbers.Count} number(s) fetched.";
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
