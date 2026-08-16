using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace IPDocketing.WinUI.Views;

/// <summary>
/// Embeds the real IP India portals via WebView2 (the same engine as Edge -
/// this is a documented Windows control, not a scraping trick). The person
/// still solves the CAPTCHA by hand in the embedded page; this page only
/// automates the OTP-typing step by reading it from their own Gmail via the
/// official Gmail API (see GmailOtpService). Nothing here touches, solves,
/// or works around the CAPTCHA itself.
///
/// SELECTOR CAVEAT: the JS in AutoFillOtp_Click that locates the OTP input
/// field is a best-guess placeholder (see tmrsearch.ipindia.gov.in's actual
/// rendered DOM was never available to inspect live while writing this -
/// same limitation flagged when the original Selenium script was built
/// earlier in this project). Open DevTools in the embedded browser
/// (F12 works in WebView2), find the OTP input's actual id/name, and update
/// the selector below - it's marked clearly.
///
/// SESSION PERSISTENCE: the browser's cookies/session are stored in a
/// dedicated folder under the app's data directory (not the default
/// per-exe location WebView2 would otherwise pick, which can be unwritable
/// or inconsistent for an unpackaged app). If IP India's own session cookie
/// outlives a single browser close - which is up to their site, not
/// something this app controls - re-opening this page can skip straight
/// back to being logged in without a fresh OTP+CAPTCHA. When their session
/// actually expires, you're back to the normal manual flow. "Clear saved
/// session" below wipes that folder if you want a clean slate (e.g. on a
/// shared machine).
/// </summary>
public sealed partial class IpIndiaPortalPage : Page
{
    private const string TrademarkSearchUrl = "https://tmrsearch.ipindia.gov.in/tmrpublicsearch";
    private const string PatentSearchUrl = "https://iprsearch.ipindia.gov.in/PublicSearch";

    private static string SessionDataFolder => Path.Combine(App.AppDataDirectory, "WebView2Session");

    public IpIndiaPortalPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitBrowserAsync();
    }

    private async System.Threading.Tasks.Task InitBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(SessionDataFolder);
            // Setting CreationProperties before first use is the WinUI
            // XAML control's own documented way to control the user data
            // folder - the control builds its own CoreWebView2Environment
            // internally from this, so it works regardless of exactly which
            // CreateAsync overload shape the resolved WebView2 SDK version
            // exposes (that overload's parameter names/count aren't stable
            // across versions, which is what broke the two previous
            // attempts here).
            Browser.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = SessionDataFolder
            };
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Navigate(TrademarkSearchUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't start the embedded browser: {ex.Message}. " +
                               "The WebView2 Runtime may need to be installed (it ships with Windows 10/11 by default).";
        }
    }

    private async void ClearSession_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            // The control holds a live handle into this folder, so it has to
            // be torn down and rebuilt before the files can actually be deleted.
            Browser.Close();

            if (Directory.Exists(SessionDataFolder))
                Directory.Delete(SessionDataFolder, recursive: true);

            StatusText.Text = "Saved session cleared. Reloading...";
            await InitBrowserAsync();
            StatusText.Text = "Session cleared - you'll need to log in fresh.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't clear the session: {ex.Message}";
        }
    }

    private void GoToTrademark_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null)
            Browser.CoreWebView2.Navigate(TrademarkSearchUrl);
    }

    private void GoToPatent_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null)
            Browser.CoreWebView2.Navigate(PatentSearchUrl);
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

        // SELECTOR TODO: 'input[id*="otp" i]' is a guess, not confirmed against
        // the live rendered page. Open DevTools (F12) in the embedded browser,
        // inspect the actual OTP field, and replace this selector.
        var script = $$"""
            (function() {
                var field = document.querySelector('input[id*="otp" i]');
                if (!field) return 'not-found';
                field.value = '{{otp}}';
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
                return 'filled';
            })();
            """;

        var result = await Browser.CoreWebView2.ExecuteScriptAsync(script);

        StatusText.Text = result.Trim('"') == "filled"
            ? $"OTP {otp} auto-filled. Review it on the page and click Verify yourself."
            : $"Found OTP {otp}, but couldn't locate the field automatically - the selector needs adjusting (see code comment). You can type it in manually: {otp}";
    }
}
