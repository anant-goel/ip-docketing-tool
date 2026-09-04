using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace IPDocketing.Core.Services;

/// <summary>
/// A purpose-built client for ASP.NET WebForms pages - "our own browser" for
/// this one site, in the only sense that is sane.
///
/// WHAT IT IS
///
/// Not a rendering engine. It does the four things a browser does that a bare
/// HttpClient does not, and that this site depends on:
///
///   1. KEEPS A SESSION. A CookieContainer across every request, so the
///      ASP.NET_SessionId set on first visit is sent back. Without it the
///      server treats each request as a new visitor and serves a different
///      page - which is the most likely reason the plain HTTP path saw rows
///      but no download links.
///
///   2. REPLAYS POSTBACKS. WebForms renders links as
///      href="javascript:__doPostBack('ctl00$target','arg')". There is no URL
///      to GET. The file only exists as the response to a form POST carrying
///      __VIEWSTATE, __VIEWSTATEGENERATOR, __EVENTVALIDATION, __EVENTTARGET
///      and __EVENTARGUMENT. This extracts those from the page and posts them
///      back exactly as the browser's own script would.
///
///   3. CARRIES REAL HEADERS. User-Agent, Accept, Accept-Language, Referer.
///      Some government front-ends serve a stripped page, or none, to a client
///      that sends no User-Agent.
///
///   4. FOLLOWS THE RESULT. A postback may return HTML, a redirect, or the file
///      itself with a Content-Disposition header. All three are handled.
///
/// WHY THIS RATHER THAN THE EMBEDDED BROWSER
///
/// It needs no WebView2, no UI thread, no window, and no visual tree - so it
/// works in the background sync, runs in milliseconds rather than seconds, and
/// cannot paint itself over the app. The embedded browser remains the fallback
/// for anything this cannot handle, because it executes script and this does
/// not.
///
/// HONEST LIMIT
///
/// If a link's target is computed by JavaScript at click time, rather than
/// being present in the markup as a __doPostBack call, this cannot see it and
/// the browser path has to take over. The postback targets found are reported,
/// so which case you are in is visible rather than guessed.
/// </summary>
public sealed class WebFormsSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();

    private string? _lastPageHtml;
    private string? _lastPageUrl;

    public WebFormsSession(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(5) };

        // Identifying honestly. This is a public page with no login and no
        // CAPTCHA; the headers are here so the server serves its normal page,
        // not to disguise what the client is.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36 IPDocketing/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-IN,en;q=0.9");
    }

    /// <summary>Names of the cookies currently held, for diagnostics.</summary>
    public string DescribeCookies(string url)
    {
        try
        {
            var found = _cookies.GetCookies(new Uri(url));
            if (found.Count == 0) return "(none)";
            return string.Join(", ", found.Cast<Cookie>().Select(c => c.Name));
        }
        catch
        {
            return "(unreadable)";
        }
    }

    /// <summary>
    /// Loads a page and remembers it, so postbacks can be built from its form.
    /// </summary>
    public async Task<string> OpenAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_lastPageUrl is not null) request.Headers.Referrer = new Uri(_lastPageUrl);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        _lastPageHtml = await response.Content.ReadAsStringAsync(ct);
        _lastPageUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return _lastPageHtml;
    }

    public sealed record PostbackTarget(string Label, string EventTarget, string EventArgument);

    /// <summary>
    /// Finds every __doPostBack link on the last loaded page.
    ///
    /// Matches both shapes WebForms emits - the target in href, and the target
    /// in an onclick handler - and takes the label from the anchor's text, or
    /// from an inner image's alt/title when the link is an icon with no text.
    /// </summary>
    public List<PostbackTarget> FindPostbackTargets()
    {
        var targets = new List<PostbackTarget>();
        if (_lastPageHtml is null) return targets;

        var document = new HtmlDocument();
        document.LoadHtml(_lastPageHtml);

        var anchors = document.DocumentNode.SelectNodes("//a");
        if (anchors is null) return targets;

        var pattern = new Regex(
            @"__doPostBack\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]*)['""]\s*\)",
            RegexOptions.IgnoreCase);

        foreach (var anchor in anchors)
        {
            var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", "") ?? "");
            var onclick = HtmlEntity.DeEntitize(anchor.GetAttributeValue("onclick", "") ?? "");

            var match = pattern.Match(href);
            if (!match.Success) match = pattern.Match(onclick);
            if (!match.Success) continue;

            targets.Add(new PostbackTarget(
                DeriveLabel(anchor, targets.Count),
                match.Groups[1].Value,
                match.Groups[2].Value));
        }

        return targets;
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

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var cleaned = Regex.Replace(HtmlEntity.DeEntitize(candidate), @"\s+", " ").Trim();
            if (cleaned.Length is > 0 and < 120) return cleaned;
        }

        return $"Link {ordinal + 1}";
    }

    public sealed record PostbackResult(
        bool IsFile,
        string? SavedPath,
        long Bytes,
        string? Html,
        string? ContentType,
        string? Error);

    /// <summary>
    /// Submits a postback and saves the response when it is a file.
    ///
    /// Every hidden input on the form is replayed, not just the well-known
    /// three. WebForms pages carry site-specific hidden state, and omitting a
    /// field the server expects produces a validation failure that looks
    /// exactly like a broken link.
    /// </summary>
    public async Task<PostbackResult> PostBackAsync(
        string eventTarget, string eventArgument, string targetPath,
        CancellationToken ct = default)
    {
        if (_lastPageHtml is null || _lastPageUrl is null)
            return new PostbackResult(false, null, 0, null, null, "No page has been opened yet.");

        var document = new HtmlDocument();
        document.LoadHtml(_lastPageHtml);

        var form = document.DocumentNode.SelectSingleNode("//form");
        if (form is null)
            return new PostbackResult(false, null, 0, null, null, "The page has no <form> to post back to.");

        var fields = new Dictionary<string, string>();

        var inputs = form.SelectNodes(".//input");
        if (inputs is not null)
        {
            foreach (var input in inputs)
            {
                var name = input.GetAttributeValue("name", "");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var type = (input.GetAttributeValue("type", "") ?? "").ToLowerInvariant();

                // Unchecked boxes and radios are not submitted by a browser,
                // so including them would send state the page never had.
                if (type is "checkbox" or "radio" &&
                    input.Attributes["checked"] is null) continue;

                if (type is "submit" or "button" or "image") continue;

                fields[name] = HtmlEntity.DeEntitize(input.GetAttributeValue("value", "") ?? "");
            }
        }

        // Selects contribute their chosen option.
        var selects = form.SelectNodes(".//select");
        if (selects is not null)
        {
            foreach (var select in selects)
            {
                var name = select.GetAttributeValue("name", "");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var selected = select.SelectSingleNode(".//option[@selected]")
                               ?? select.SelectSingleNode(".//option");
                if (selected is null) continue;

                fields[name] = HtmlEntity.DeEntitize(
                    selected.GetAttributeValue("value", selected.InnerText?.Trim() ?? ""));
            }
        }

        // The two that drive which control was "clicked".
        fields["__EVENTTARGET"] = eventTarget;
        fields["__EVENTARGUMENT"] = eventArgument;

        var action = HtmlEntity.DeEntitize(form.GetAttributeValue("action", "") ?? "");
        var postUrl = string.IsNullOrWhiteSpace(action) || action == "./"
            ? _lastPageUrl
            : new Uri(new Uri(_lastPageUrl), action).ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Referrer = new Uri(_lastPageUrl);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            return new PostbackResult(false, null, 0, null, null, $"HTTP {(int)response.StatusCode}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var disposition = response.Content.Headers.ContentDisposition?.DispositionType ?? "";

        var looksLikeFile =
            contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
            disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeFile)
        {
            // HTML back: the postback navigated rather than downloading. Keep
            // it as the current page so a follow-up postback can be built from
            // it - that is how multi-step WebForms flows work.
            var html = await response.Content.ReadAsStringAsync(ct);
            _lastPageHtml = html;
            _lastPageUrl = response.RequestMessage?.RequestUri?.ToString() ?? postUrl;

            return new PostbackResult(false, null, 0, html, contentType,
                "The postback returned a page rather than a file.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var temp = targetPath + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var destination = File.Create(temp))
        {
            await source.CopyToAsync(destination, ct);
        }

        var length = new FileInfo(temp).Length;

        // A few kilobytes is an error page with a PDF content-type, which does
        // happen when the file store is down.
        if (length < 20_000)
        {
            try { File.Delete(temp); } catch { }
            return new PostbackResult(false, null, length, null, contentType,
                $"Only {length} bytes arrived - an error response, not a document.");
        }

        // Size is a proxy; the header is the answer. A 30 KB ASP.NET error page
        // served as application/octet-stream clears the floor above comfortably,
        // and once it is cached under a .pdf name every later run reads it
        // instead of the journal.
        if (!LooksLikePdf(temp))
        {
            try { File.Delete(temp); } catch { }
            return new PostbackResult(false, null, length, null, contentType,
                $"{length:N0} bytes arrived but the file has no PDF header - an error page, not a document.");
        }

        File.Move(temp, targetPath, overwrite: true);
        return new PostbackResult(true, targetPath, length, null, contentType, null);
    }

    /// <summary>Plain GET download, for links that do have a real URL.</summary>
    public async Task<PostbackResult> DownloadAsync(
        string url, string targetPath, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_lastPageUrl is not null) request.Headers.Referrer = new Uri(_lastPageUrl);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return new PostbackResult(false, null, 0, null, null, $"HTTP {(int)response.StatusCode}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return new PostbackResult(false, null, 0, null, contentType,
                "The server returned a page rather than a file - the session may have lapsed.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var temp = targetPath + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var destination = File.Create(temp))
        {
            await source.CopyToAsync(destination, ct);
        }

        var length = new FileInfo(temp).Length;
        if (length < 20_000)
        {
            try { File.Delete(temp); } catch { }
            return new PostbackResult(false, null, length, null, contentType,
                $"Only {length} bytes arrived - an error page, not a document.");
        }

        if (!LooksLikePdf(temp))
        {
            try { File.Delete(temp); } catch { }
            return new PostbackResult(false, null, length, null, contentType,
                $"{length:N0} bytes arrived but the file has no PDF header - an error page, not a document.");
        }

        File.Move(temp, targetPath, overwrite: true);
        return new PostbackResult(true, targetPath, length, null, contentType, null);
    }

    /// <summary>True when the file actually begins with "%PDF-".</summary>
    private static bool LooksLikePdf(string path)
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

    public string? CurrentHtml => _lastPageHtml;

    public void Dispose() => _http.Dispose();
}
