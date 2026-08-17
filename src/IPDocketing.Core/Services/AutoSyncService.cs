using IPDocketing.Core.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.Core.Services;

/// <summary>
/// The unattended pipeline: discover new Journal issues, download the PDFs,
/// pull the text out, parse the published marks, run the watch, raise alerts.
/// No clicks, no CAPTCHA, no login.
///
/// WHY THIS CAN BE FULLY AUTOMATIC WHEN THE REST CANNOT
///
/// The Trade Marks Journal listing and its PDFs are published openly - no
/// account, no OTP, no CAPTCHA, no rate gate. Reading a public government
/// publication on a schedule is exactly what it is published for. That is a
/// completely different thing from the register search at tmrsearch, which sits
/// behind a CAPTCHA precisely because the Registry has decided automated bulk
/// access to it is not on offer. This service does the first and never touches
/// the second.
///
/// So the honest split is:
///
///   FULLY AUTOMATIC (this service)  - journal discovery, PDF download, text
///                                     extraction/OCR, mark parsing, watch,
///                                     conflict alerts, opposition-deadline
///                                     docketing off the publication date.
///   ONE SOLVE THEN UNATTENDED       - e-Status bulk fetch, document download.
///                                     You solve one CAPTCHA; the queue then
///                                     runs through hundreds of numbers inside
///                                     that session.
///   NEVER AUTOMATED                 - the CAPTCHA itself, and CEFS sign-in.
///
/// Everything here is resumable and idempotent. Each stage records its own
/// completion timestamp on the issue row, so an interrupted run picks up where
/// it stopped rather than re-downloading a hundred megabytes of PDFs.
/// </summary>
public class AutoSyncService : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalService _journal;
    private readonly JournalFetchService _fetch;
    private readonly WatchService _watch;
    private readonly AuditService _audit;
    private readonly JournalMarkParser _parser = new();
    private readonly string _libraryPath;

    private IDocumentTextExtractor? _extractor;
    private Timer? _timer;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>How often the unattended pass runs. The Journal is weekly, so hourly is generous.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Cap on PDFs fetched per pass, so a first run doesn't pull years of back issues at once.</summary>
    public int MaxDownloadsPerRun { get; set; } = 4;

    /// <summary>How many recent issues to consider on each discovery pass.</summary>
    public int IssuesToTrack { get; set; } = 8;

    public bool IsRunning { get; private set; }
    public DateTime? LastRunUtc { get; private set; }
    public string LastStatus { get; private set; } = "Not run yet.";

    /// <summary>Raised after each stage so the UI can show progress without polling.</summary>
    public event Action<string>? Progress;

    public AutoSyncService(
        AppDbContext db, JournalService journal, JournalFetchService fetch,
        WatchService watch, AuditService audit, string libraryPath)
    {
        _db = db;
        _journal = journal;
        _fetch = fetch;
        _watch = watch;
        _audit = audit;
        _libraryPath = libraryPath;
        Directory.CreateDirectory(_libraryPath);
    }

    /// <summary>
    /// Supplies the PDF reader. Injected rather than constructed here because
    /// the OCR half depends on WinRT APIs that only the WinUI project can see -
    /// Core stays free of a UI dependency.
    /// </summary>
    public void UseExtractor(IDocumentTextExtractor extractor) => _extractor = extractor;

    public void Start()
    {
        _timer?.Dispose();
        // First pass shortly after launch rather than immediately, so startup
        // isn't competing with a network call and a PDF parse.
        _timer = new Timer(async _ => await RunOnceAsync(), null,
            TimeSpan.FromMinutes(2), Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public sealed record SyncReport(
        int IssuesDiscovered,
        int PdfsDownloaded,
        int IssuesExtracted,
        int MarksParsed,
        int AlertsRaised,
        List<string> Notes);

    /// <summary>
    /// One full pass. Safe to call concurrently - overlapping calls return
    /// immediately rather than queueing, since a second pass would only fight
    /// the first over the same files.
    /// </summary>
    public async Task<SyncReport> RunOnceAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();

        if (!await _runGate.WaitAsync(0, ct))
            return new SyncReport(0, 0, 0, 0, 0, new List<string> { "A sync is already running." });

        IsRunning = true;
        var discovered = 0;
        var downloaded = 0;
        var extracted = 0;
        var marksParsed = 0;
        var alerts = 0;

        try
        {
            // --- 1. Discover issues -------------------------------------
            Report("Checking for new Journal issues...");
            try
            {
                var latest = await _fetch.GetLatestIssuesAsync(IssuesToTrack, ct);
                var known = _journal.GetAll()
                    .Select(j => j.IssueNumber)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in latest)
                {
                    if (known.Contains(entry.JournalNumber)) continue;

                    var link = entry.ClassLinks.FirstOrDefault();
                    _journal.Add(new JournalIssue
                    {
                        IssueNumber = entry.JournalNumber,
                        PublicationDate = entry.PublicationDate ?? DateTime.Today,
                        Url = link.PdfUrl ?? string.Empty,
                        Notes = entry.ClassLinks.Count == 0
                            ? "No class-range links advertised"
                            : $"{entry.ClassLinks.Count} class-range PDF link(s)"
                    });
                    discovered++;
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Issue discovery failed: {ex.Message}");
            }

            // --- 2. Download PDFs ---------------------------------------
            var pending = _db.JournalIssues
                .Where(j => j.LocalPdfPath == null && j.Url != "")
                .OrderByDescending(j => j.PublicationDate)
                .Take(MaxDownloadsPerRun)
                .ToList();

            foreach (var issue in pending)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Downloading Journal {issue.IssueNumber}...");
                try
                {
                    var path = await _fetch.DownloadPdfAsync(issue.Url, _libraryPath, issue.IssueNumber, ct);
                    var info = new FileInfo(path);

                    // A "PDF" of a few kilobytes is nearly always an error page
                    // saved with a .pdf name. Catching it here stops the
                    // extractor producing confident nonsense from an HTML
                    // maintenance notice.
                    if (info.Length < 20_000)
                    {
                        issue.LastError = $"Downloaded file was only {info.Length} bytes - likely an error page, not the Journal.";
                        try { File.Delete(path); } catch { /* ignore */ }
                    }
                    else
                    {
                        issue.LocalPdfPath = path;
                        issue.PdfSizeBytes = info.Length;
                        issue.DownloadedUtc = DateTime.UtcNow;
                        issue.LastError = null;
                        downloaded++;
                    }
                }
                catch (Exception ex)
                {
                    issue.LastError = $"Download failed: {ex.Message}";
                    notes.Add($"Journal {issue.IssueNumber}: {ex.Message}");
                }
                _db.SaveChanges();
            }

            // --- 3. Extract text + parse marks + run watch --------------
            if (_extractor is null)
            {
                notes.Add("No PDF reader is wired up, so downloaded issues were not read.");
            }
            else
            {
                var toRead = _db.JournalIssues
                    .Where(j => j.LocalPdfPath != null && j.WatchRunUtc == null)
                    .OrderByDescending(j => j.PublicationDate)
                    .Take(MaxDownloadsPerRun)
                    .ToList();

                foreach (var issue in toRead)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(issue.LocalPdfPath!))
                    {
                        issue.LocalPdfPath = null;
                        _db.SaveChanges();
                        continue;
                    }

                    Report($"Reading Journal {issue.IssueNumber}...");
                    try
                    {
                        var result = await _extractor.ExtractAsync(issue.LocalPdfPath!, ct);
                        issue.ExtractionMethod = result.Method;
                        issue.TextExtractedUtc = DateTime.UtcNow;

                        if (!result.Succeeded)
                        {
                            issue.LastError = result.Error ?? "No text could be read from this PDF.";
                            _db.SaveChanges();
                            notes.Add($"Journal {issue.IssueNumber}: {issue.LastError}");
                            continue;
                        }

                        extracted++;

                        var parsed = _parser.Parse(result.Text, fromOcr: !result.IsExact);
                        issue.MarksParsed = parsed.Count;
                        marksParsed += parsed.Count;

                        if (parsed.Count == 0)
                        {
                            issue.LastError = "Text was read but no published marks could be parsed out of it. " +
                                              "The Journal layout may have changed.";
                            _db.SaveChanges();
                            notes.Add($"Journal {issue.IssueNumber}: no marks parsed from {result.PageCount} page(s).");
                            continue;
                        }

                        Report($"Running watch against {parsed.Count} published mark(s)...");
                        // Class and the OCR flag are passed through, so the
                        // matcher can weight for proximity of goods and be
                        // tolerant of characters OCR misreads.
                        var raised = _watch.RunWatch(
                            issue.Id,
                            parsed.Select(p => (p.Mark, (string?)p.Proprietor, p.NiceClass)),
                            fromOcr: !result.IsExact);

                        alerts += raised.Count;

                        var lowConfidence = parsed.Count(p => p.NeedsReview);
                        issue.WatchRunUtc = DateTime.UtcNow;
                        issue.LastError = null;
                        issue.Notes = $"{parsed.Count} mark(s) parsed via {result.Method}" +
                                      (lowConfidence > 0 ? $"; {lowConfidence} low-confidence and worth eyeballing" : "") +
                                      $". {raised.Count} conflict alert(s).";
                        _db.SaveChanges();
                    }
                    catch (Exception ex)
                    {
                        issue.LastError = $"Read failed: {ex.Message}";
                        _db.SaveChanges();
                        notes.Add($"Journal {issue.IssueNumber}: {ex.Message}");
                    }
                }
            }

            LastRunUtc = DateTime.UtcNow;
            LastStatus = discovered + downloaded + extracted + alerts == 0
                ? "Nothing new since the last check."
                : $"{discovered} new issue(s), {downloaded} downloaded, {extracted} read, {alerts} alert(s) raised.";

            if (downloaded > 0 || alerts > 0)
                _audit.Log("Sync", "Journal", 0,
                    $"Automatic sync: {discovered} discovered, {downloaded} downloaded, " +
                    $"{marksParsed} marks parsed, {alerts} alerts raised.");

            Report(LastStatus);
            return new SyncReport(discovered, downloaded, extracted, marksParsed, alerts, notes);
        }
        catch (OperationCanceledException)
        {
            LastStatus = "Sync cancelled.";
            return new SyncReport(discovered, downloaded, extracted, marksParsed, alerts, notes);
        }
        catch (Exception ex)
        {
            LastStatus = $"Sync failed: {ex.Message}";
            notes.Add(LastStatus);
            return new SyncReport(discovered, downloaded, extracted, marksParsed, alerts, notes);
        }
        finally
        {
            IsRunning = false;
            _runGate.Release();
        }
    }

    private void Report(string message)
    {
        LastStatus = message;
        Progress?.Invoke(message);
    }

    public void Dispose()
    {
        Stop();
        _runGate.Dispose();
    }
}
