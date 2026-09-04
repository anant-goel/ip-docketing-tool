namespace IPDocketing.Core.Services;

/// <summary>
/// Where Journal data comes from.
///
/// WHY THIS ABSTRACTION EXISTS
///
/// There were two independent ways to reach the Journal listing, and they did
/// not behave the same:
///
///   HTTP  - HttpClient + HtmlAgilityPack. No JavaScript, no cookies, no
///           session. Used by seven call sites: auto-fetch, the weekly pull,
///           browse, the background sync, and the name search's downloader.
///   BROWSER - a real WebView2. Used by exactly one feature.
///
/// The HTTP path consistently returned rows but zero links. That is the
/// signature of a page whose table is server-rendered while its download column
/// depends on something a bare HTTP client does not have - a session cookie set
/// on first visit, or markup written by script after load. No amount of better
/// parsing fixes a document that never contained the links.
///
/// So the browser becomes the primary source and HTTP the fallback, and this
/// interface is what lets Core services use it. Core targets plain
/// net8.0-windows and cannot see WebView2, which lives in the WinUI project -
/// the implementation is injected from there.
/// </summary>
public interface IJournalSource
{
    /// <summary>A short name for the mechanism, so reports can say which one produced a result.</summary>
    string SourceName { get; }

    /// <summary>Every issue on the listing, with all of its download links.</summary>
    Task<List<JournalSourceIssue>> ListIssuesAsync(CancellationToken ct = default);

    /// <summary>
    /// Obtains one file. Implementations that cannot address a link by URL
    /// (because it is a postback) click it and capture the resulting download.
    /// Returns the saved path, or null with a reason.
    /// </summary>
    Task<JournalDownloadResult> DownloadAsync(
        JournalSourceIssue issue,
        JournalSourceLink link,
        string targetPath,
        CancellationToken ct = default);

    /// <summary>
    /// Finds an issue by number and downloads its files into a directory.
    ///
    /// This is what the name search and the background sync need: they hold a
    /// journal NUMBER from the database, not a live link, and a link captured
    /// on a previous visit may no longer be valid in a new session. Resolving
    /// the number against the live listing each time avoids depending on a
    /// stored URL that may never have been fetchable in the first place.
    /// </summary>
    Task<List<JournalDownloadResult>> DownloadIssueAsync(
        string journalNumber,
        string targetDirectory,
        int maxFiles = 8,
        CancellationToken ct = default);
}

public sealed record JournalSourceLink(string Label, string? Url, int ElementIndex)
{
    /// <summary>True when the link has no address and can only be actioned by clicking.</summary>
    public bool RequiresClick => string.IsNullOrWhiteSpace(Url);
}

public sealed record JournalSourceIssue(
    string JournalNumber,
    DateTime? PublicationDate,
    List<JournalSourceLink> Links);

public sealed record JournalDownloadResult(
    bool Saved,
    string? FilePath,
    long Bytes,
    string? Error)
{
    public static JournalDownloadResult Success(string path, long bytes) =>
        new(true, path, bytes, null);

    public static JournalDownloadResult Failure(string reason) =>
        new(false, null, 0, reason);
}
