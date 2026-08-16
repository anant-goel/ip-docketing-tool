using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace IPDocketing.Core.Services;

/// <summary>
/// Reads OTP codes out of your own Gmail inbox via the official Gmail API,
/// using OAuth you grant explicitly on first run. This only automates a
/// step you're already authorized to do yourself (reading an email sent to
/// you) - it does not touch, solve, or bypass IP India's CAPTCHA, which
/// remains a manual click in the embedded browser. See IpIndiaPortalPage.
///
/// SETUP REQUIRED (one-time, on your Google account, before this works):
///   1. Go to console.cloud.google.com, create a project (or reuse one).
///   2. APIs & Services > Library > enable "Gmail API".
///   3. APIs & Services > Credentials > Create Credentials > OAuth client ID
///      > Application type "Desktop app". Download the JSON.
///   4. Rename the downloaded file to gmail_client_secret.json and place it
///      in the app's data folder (%LocalAppData%\IPDocketing\).
///   5. First call to AuthorizeAsync opens a browser consent screen once;
///      the resulting token is cached locally for future runs.
/// I can't do steps 1-4 for you - they require your own Google account and
/// can't be generated blind.
/// </summary>
public class GmailOtpService
{
    private static readonly string[] Scopes = { GmailService.Scope.GmailReadonly };
    private readonly string _clientSecretPath;
    private readonly string _tokenStorePath;
    private GmailService? _service;

    public GmailOtpService(string appDataDirectory)
    {
        _clientSecretPath = Path.Combine(appDataDirectory, "gmail_client_secret.json");
        _tokenStorePath = Path.Combine(appDataDirectory, "gmail_token_store");
    }

    public bool IsConfigured => File.Exists(_clientSecretPath);

    private async Task<GmailService> GetServiceAsync(CancellationToken ct)
    {
        if (_service is not null) return _service;

        if (!IsConfigured)
            throw new InvalidOperationException(
                $"Gmail isn't set up yet - place your OAuth client JSON at {_clientSecretPath} (see GmailOtpService setup comment).");

        await using var stream = new FileStream(_clientSecretPath, FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets,
            Scopes,
            "ipdocketing-user",
            ct,
            new FileDataStore(_tokenStorePath, true));

        _service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "IP Docketing"
        });
        return _service;
    }

    /// <summary>
    /// Searches recent mail for an OTP from IP India and returns the numeric
    /// code, or null if nothing matching turned up within the last few
    /// minutes. Caller is expected to poll this a few times after clicking
    /// "Send OTP" in the embedded browser, since email delivery isn't
    /// instant.
    /// </summary>
    public async Task<string?> FindRecentOtpAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var service = await GetServiceAsync(ct);

        // newer_than:15m keeps the search fast and avoids ever matching an
        // old, stale OTP email from a prior session.
        var minutes = Math.Max(1, (int)maxAge.TotalMinutes);
        var request = service.Users.Messages.List("me");
        request.Q = $"newer_than:{minutes}m (from:ipindia.gov.in OR subject:OTP OR subject:\"One Time Password\")";
        request.MaxResults = 5;

        var listResponse = await request.ExecuteAsync(ct);
        if (listResponse.Messages is null) return null;

        foreach (var messageRef in listResponse.Messages)
        {
            var message = await service.Users.Messages.Get("me", messageRef.Id).ExecuteAsync(ct);
            var bodyText = ExtractPlainText(message);
            var match = Regex.Match(bodyText, @"\b(\d{4,8})\b");
            if (match.Success) return match.Groups[1].Value;
        }

        return null;
    }

    private static string ExtractPlainText(Google.Apis.Gmail.v1.Data.Message message)
    {
        var snippet = message.Snippet ?? "";
        var parts = message.Payload?.Parts;
        if (parts is null || parts.Count == 0) return snippet;

        foreach (var part in parts)
        {
            if (part.MimeType == "text/plain" && part.Body?.Data is not null)
            {
                var bytes = Convert.FromBase64String(part.Body.Data.Replace('-', '+').Replace('_', '/'));
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        return snippet;
    }
}
