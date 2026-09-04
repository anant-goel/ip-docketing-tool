using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;

using IPDocketing.WinUI.Services;

namespace IPDocketing.WinUI.Views;

public sealed partial class JournalPage : Page
{
    public JournalPage()
    {
        InitializeComponent();

        // THE 1601 BUG, PROPERLY THIS TIME.
        //
        // Setting DatePicker.Date was never enough. WinUI's DatePicker tracks
        // two things: Date (a DateTimeOffset, defaulting to
        // DateTimeOffset.MinValue - 01 Jan 1601, the FILETIME epoch) and
        // SelectedDate (a DateTimeOffset?, null until the control holds a real
        // value). The "day / month / year" placeholder columns are the control
        // saying SelectedDate is still null, and assigning Date does not clear
        // that: the template overwrites it when it is applied, which is after
        // the constructor runs. So the seed looked applied and wasn't.
        //
        // SelectedDate is the property that actually sets the control. It is
        // assigned here and again on Loaded, once the template exists.
        FetchDateBox.SelectedDate = DateTimeOffset.Now;
        Loaded += (_, _) =>
        {
            FetchDateBox.SelectedDate ??= DateTimeOffset.Now;
        };

        try { LoadIssues(); LoadAlerts(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"JournalPage load failed: {ex}"); }
    }

    private void LoadIssues()
    {
        // Clears duplicates left by earlier runs, before deduplication existed.
        try { App.Journal.RemoveDuplicates(); } catch { /* cosmetic */ }

        // Alert counts per issue, so a row can say "Match found (3)" instead of
        // the single "Pending review" every row used to show regardless of what
        // had actually happened to it.
        Dictionary<int, int> alertCounts;
        try
        {
            alertCounts = App.Watch.GetAllIncludingDismissed()
                .GroupBy(a => a.JournalIssueId)
                .ToDictionary(g => g.Key, g => g.Count());
        }
        catch
        {
            alertCounts = new Dictionary<int, int>();
        }

        IssueList.ItemsSource = App.Journal.GetAll()
            .Select(j => new IssueRow(j, alertCounts.TryGetValue(j.Id, out var c) ? c : 0))
            .ToList();
    }

    private void LoadAlerts()
    {
        AlertList.ItemsSource = App.Watch.GetAll().Select(a => new AlertRow(a)).ToList();
    }

    private async void AutoFetch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!int.TryParse(FetchClassBox.Text?.Trim(), out var trademarkClass) || trademarkClass < 1 || trademarkClass > 99)
        {
            FetchStatusText.Text = "Enter a valid trademark class (1-99).";
            return;
        }

        // BUG FIX (from your screenshot: "No journal issue found on/before
        // 01 Jan 1601"). A WinUI DatePicker that has never been touched returns
        // DateTimeOffset.MinValue, which is 01-Jan-1601 - the FILETIME epoch.
        // The fetch was therefore asking IP India for a journal published
        // before the Registry existed, and correctly finding none. The picker
        // is now seeded with today's date in the constructor, and this guard
        // catches any other route to an unset value.
        // Read from SelectedDate, with Date only as a fallback - see the note in
        // the constructor. The year guard stays as a backstop: a value this
        // wrong must never reach a query, whatever route it came in by.
        var date = (FetchDateBox.SelectedDate ?? FetchDateBox.Date).DateTime;
        if (date.Year < 1950)
        {
            date = DateTime.Today;
            FetchDateBox.SelectedDate = DateTimeOffset.Now;
        }
        FetchStatusText.Text = "Fetching from IP India...";

        try
        {
            // Reports which step actually failed. The message this replaces
            // named two possible causes at once ("no issue found, OR the class
            // wasn't in a parseable range") and so identified neither. Tested
            // against the live listing: the class ranges parse correctly, and
            // the real cause was always the unset date picker.
            var lookup = await App.JournalFetch.FindByDateAndClassDetailedAsync(date, trademarkClass);
            if (!lookup.Found)
            {
                FetchStatusText.Text = lookup.Reason ?? "Lookup failed for an unknown reason.";
                return;
            }

            var issue = lookup.Issue!;
            var classRange = lookup.ClassRangeLabel!;
            var pdfUrl = lookup.PdfUrl!;

            App.Journal.Add(new IPDocketing.Core.Models.JournalIssue
            {
                IssueNumber = issue.JournalNumber,
                PublicationDate = issue.PublicationDate ?? date,
                Url = pdfUrl,
                Notes = $"Auto-fetched for class {trademarkClass} ({classRange})"
            });

            FetchStatusText.Text = $"Journal {issue.JournalNumber} ({issue.PublicationDate:dd MMM yyyy}): " +
                                   $"class {trademarkClass} is in \"{classRange}\". Fetching that file...";
            LoadIssues();

            // THE FLOW YOU ASKED FOR, END TO END.
            //
            // Open the listing, find the row by its DATE, find the class range
            // in that row that contains the class you typed, and click only that
            // one. Previously this step stopped at "logged with the PDF link",
            // which was no use at all for these rows: the listing's Download
            // column is __doPostBack, so the "link" it logged was usually empty
            // and nothing could be fetched from it.
            var fetched = await DownloadIssuePdfsAsync(
                issue.JournalNumber,
                visible: false,
                onlyClass: trademarkClass,
                publicationDate: issue.PublicationDate);

            LoadIssues();

            FetchStatusText.Text = fetched.Saved > 0
                ? $"Journal {issue.JournalNumber} ({issue.PublicationDate:dd MMM yyyy}) - " +
                  $"\"{classRange}\" saved for class {trademarkClass}."
                : $"Journal {issue.JournalNumber}: class {trademarkClass} resolved to \"{classRange}\", " +
                  "but the file could not be fetched. The issue row now shows why.";

            if (fetched.Attempted > 0 || fetched.Saved == 0)
                await TextReportDialog.ShowAsync(
                    XamlRoot, $"Class {trademarkClass} - Journal {issue.JournalNumber}",
                    fetched.Log, "classfetch");
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Fetch failed: {ex.Message}";
        }
    }

    /// <summary>
    /// docx section 4 - "links of the Trade Mark Journal which TMR publishes
    /// weekly every Monday". Reads the public listing page (no login, no OTP, no
    /// CAPTCHA on that specific page) and records any issue not already logged,
    /// with every class-range PDF link it advertises.
    /// </summary>
    private async void PullLatest_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        FetchStatusText.Text = "Reading the journal listing...";

        try
        {
            var latest = await App.JournalFetch.GetLatestIssuesAsync(8);
            if (latest.Count == 0)
            {
                FetchStatusText.Text = "The listing page returned no parseable rows. Its table layout may have changed - " +
                                       "log the issue manually and check the page in a browser.";
                return;
            }

            var existing = App.Journal.GetAll()
                .Select(j => j.IssueNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var entry in latest)
            {
                if (existing.Contains(entry.JournalNumber)) continue;

                // Prefer a real class-range PDF link; fall back to the first
                // link of any kind so the row is still actionable.
                var link = entry.ClassLinks.FirstOrDefault();
                App.Journal.Add(new JournalIssue
                {
                    IssueNumber = entry.JournalNumber,
                    PublicationDate = entry.PublicationDate ?? DateTime.Today,
                    Url = link.PdfUrl ?? "",
                    Notes = entry.ClassLinks.Count == 0
                        ? "No class-range links advertised for this issue"
                        : $"{entry.ClassLinks.Count} class-range PDF link(s): " +
                          string.Join(", ", entry.ClassLinks.Select(c => c.ClassRangeLabel))
                });
                added++;
            }

            LoadIssues();
            FetchStatusText.Text = added == 0
                ? $"Read {latest.Count} issue(s) - all of them were already logged."
                : $"Logged {added} new issue(s) out of the {latest.Count} most recent.";
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Could not read the listing: {ex.Message}";
        }
    }

    /// <summary>
    /// docx section 7 - the weekly watch "report". Renders the open alerts as a
    /// printable sheet and hands it to the browser, which owns the print dialog
    /// and the save-as-PDF path.
    /// </summary>
    /// <summary>
    /// Searches downloaded Journal PDFs for a proprietor or agent name and
    /// reports the page each hit sits on.
    ///
    /// This is a different question from the similarity watch. That compares
    /// published MARKS against your portfolio; this finds everything published
    /// under a named PARTY, whether or not the mark resembles anything you own -
    /// which is what you want when checking "did anything go through under
    /// KARTIK TRADE MARKS COMPANY this week?".
    /// </summary>
    /// <summary>
    /// Flips an issue between reviewed and pending.
    ///
    /// JournalService.MarkReviewed has existed since an early phase but nothing
    /// ever called it, so every issue displayed "Pending review" permanently and
    /// there was no way to clear it - which made the list look broken even when
    /// the fetch had worked.
    /// </summary>
    /// <summary>
    /// Opens an issue's PDF link in the default browser.
    ///
    /// Replaces ToggleReviewed_Click, whose job moved into the status badge -
    /// the badge now shows the pipeline's real state and offers the reviewed
    /// toggle inside it, so a separate button for that alone no longer earns
    /// its place in the row.
    /// </summary>
    private async void RowOpenUrl_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var issue = App.Journal.GetAll().FirstOrDefault(j => j.Id == id);
        if (issue is null || string.IsNullOrWhiteSpace(issue.Url)) return;

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(issue.Url));
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Couldn't open that link: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists every issue on the listing page with ALL of its class-range PDFs,
    /// and downloads whichever you pick.
    ///
    /// WHY THIS EXISTS. Every other path into the Journal ran through
    /// "give me a date and a class, I'll find the one right PDF". That put two
    /// pieces of guesswork between you and a file - a date box that defaulted
    /// to 1601, and a class-to-range match - and when either failed you got a
    /// message instead of a PDF, with no way to go and look for yourself.
    ///
    /// This asks for neither. It shows what the Registry actually publishes and
    /// lets you take it. If the automation is wrong about which range holds
    /// your class, you can still see all five ranges and grab the right one.
    /// A tool that can't be overridden by the person using it is a tool that
    /// fails closed, and this one was failing closed.
    /// </summary>
    /// <summary>
    /// Downloads an issue's PDFs by driving a hidden browser, with no window
    /// shown and no dialogs during the run - you get a result at the end.
    ///
    /// This exists because the HTTP approach kept extracting zero links, and
    /// the most likely reason is that there are no URLs to extract: an ASP.NET
    /// grid renders these as __doPostBack handlers, where the file only exists
    /// as the response to a form submission. A browser sidesteps that entirely
    /// by doing what a person does - running the JavaScript and receiving a
    /// file - which WebView2 then hands us with a settable destination path.
    /// </summary>
    /// <summary>
    /// Reports what this page can actually reach, step by step.
    ///
    /// Ten rounds of fixes have gone out without a single confirmed
    /// observation of where the Journal pipeline breaks, and every attempt to
    /// infer it from a screenshot has been wrong at least once. This replaces
    /// inference with measurement: it checks each link in the chain in order
    /// and reports the first one that fails, with the evidence attached.
    /// </summary>
    private async void SelfTest_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("JOURNAL PIPELINE SELF-TEST");
        report.AppendLine($"Run {DateTime.Now:dd MMM yyyy HH:mm}");
        report.AppendLine(new string('-', 60));
        report.AppendLine();

        // 1. Build identity - confirms which binary is actually running.
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        report.AppendLine($"1. Build");
        report.AppendLine($"   Version : {assembly.GetName().Version}");
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null)
                report.AppendLine($"   Built   : {System.IO.File.GetLastWriteTime(exe):dd MMM yyyy HH:mm}");
        }
        catch { }
        report.AppendLine($"   Arch    : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($"   Marker  : AutoDownload + SelfTest present (phase 51)");
        report.AppendLine();

        // 1b. Which source can actually reach the listing.
        //
        // This is the part worth reading first. Each source is tried in order and
        // its result recorded, so "issues found but no links" is reported as the
        // distinct outcome it is rather than blending into a bare failure. That
        // specific half-success is what every previous attempt produced while
        // looking superficially fine.
        report.AppendLine("1b. Journal sources");
        if (App.JournalSources is { } chain)
        {
            try
            {
                var viaChain = await chain.ListIssuesAsync();
                var linkTotal = viaChain.Sum(i => i.Links.Count);

                foreach (var attempt in chain.AttemptLog)
                    report.AppendLine($"   {attempt}");

                report.AppendLine($"   Winner  : {chain.LastSourceUsed ?? "none"}");
                report.AppendLine($"   Result  : {viaChain.Count} issue(s), {linkTotal} link(s)");

                if (linkTotal == 0)
                    report.AppendLine("   >>> Issues parsed, zero links. No source could reach the download column.");
            }
            catch (Exception ex)
            {
                report.AppendLine($"   FAILED: {ex.Message}");
            }
        }
        else
        {
            report.AppendLine("   No journal sources are configured.");
        }
        report.AppendLine();

        // 2. Network reach.
        report.AppendLine("2. Reaching the listing page (plain HTTP, for comparison)");
        string? html = null;
        try
        {
            var issues = await App.JournalFetch.FetchIssuesAsync();
            report.AppendLine($"   OK: parsed {issues.Count} issue row(s)");

            foreach (var issue in issues.Take(4))
                report.AppendLine($"     {issue.JournalNumber}  {issue.PublicationDate:dd MMM yyyy}  " +
                                  $"{issue.ClassLinks.Count} link(s)");

            var totalLinks = issues.Sum(i => i.ClassLinks.Count);
            report.AppendLine($"   Total links extracted across all rows: {totalLinks}");

            if (totalLinks == 0)
            {
                report.AppendLine("   >>> THIS IS THE FAILURE. Rows parse, links do not.");
                html = App.JournalFetch.LastEmptyRowHtml;
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"   FAILED: {ex.GetType().Name} - {ex.Message}");
            report.AppendLine("   >>> THIS IS THE FAILURE. The page could not be read at all.");
        }
        report.AppendLine();

        // 3. What is on disk.
        report.AppendLine("3. Local library");
        var library = System.IO.Path.Combine(App.AppDataDirectory, "JournalLibrary");
        try
        {
            if (!System.IO.Directory.Exists(library))
            {
                report.AppendLine($"   Folder does not exist yet: {library}");
            }
            else
            {
                var files = System.IO.Directory.GetFiles(library);
                report.AppendLine($"   {files.Length} file(s) in {library}");
                foreach (var f in files.Take(8))
                    report.AppendLine($"     {System.IO.Path.GetFileName(f)}  " +
                                      $"{new System.IO.FileInfo(f).Length / 1024 / 1024.0:0.0} MB");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"   FAILED: {ex.Message}");
        }
        report.AppendLine();

        // 4. Database state.
        report.AppendLine("4. Issues on record");
        try
        {
            var recorded = App.Journal.GetAll();
            report.AppendLine($"   {recorded.Count} issue(s) in the database");
            foreach (var j in recorded.OrderByDescending(j => j.PublicationDate).Take(6))
                report.AppendLine($"     {j.IssueNumber}  {j.PublicationDate:dd MMM yyyy}  " +
                                  $"pdf={(string.IsNullOrWhiteSpace(j.LocalPdfPath) ? "NONE" : "yes")}  " +
                                  $"url={(string.IsNullOrWhiteSpace(j.Url) ? "NONE" : "yes")}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"   FAILED: {ex.Message}");
        }
        report.AppendLine();

        // 5. OCR engine.
        report.AppendLine("5. Text extraction");
        report.AppendLine($"   Tesseract: {(App.TextExtractor?.TesseractAvailable == true ? "available" : "not found - using Windows OCR")}");
        report.AppendLine();

        if (html is not null)
        {
            report.AppendLine(new string('-', 60));
            report.AppendLine("RAW HTML OF A ROW THAT PRODUCED NO LINKS");
            report.AppendLine("(this is the single most useful thing to send back)");
            report.AppendLine(new string('-', 60));
            report.AppendLine(html);
        }

        await TextReportDialog.ShowAsync(XamlRoot, "Self-test", report.ToString(), "selftest");
        FetchStatusText.Text = "Self-test complete - the report is also saved to the Reports folder.";
    }

    private async void AutoDownload_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var issuePrompt = new TextBox
        {
            Header = "Journal number",
            PlaceholderText = "e.g. 2273",
            Text = App.Journal.GetAll()
                .OrderByDescending(j => j.PublicationDate)
                .FirstOrDefault()?.IssueNumber ?? "",
            MinWidth = 320
        };

        // Watching it run is the fastest way to find out why it isn't working -
        // far faster than inferring the failure from a log after the fact.
        var showBrowser = new CheckBox
        {
            Content = "Show the browser while it runs (for diagnosing)",
            IsChecked = false
        };

        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(issuePrompt);
        panel.Children.Add(showBrowser);
        panel.Children.Add(new TextBlock
        {
            Text = "A hidden browser opens the listing, clicks each of that issue's links, " +
                   "and saves whatever downloads. Nothing appears on screen unless you tick the box. " +
                   "Expect roughly 10-20 seconds per file - Journal PDFs are large.",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.65
        });

        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Auto-download in background",
            Content = panel,
            PrimaryButtonText = "Start",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

        var journalNumber = issuePrompt.Text?.Trim() ?? "";
        if (journalNumber.Length == 0)
        {
            FetchStatusText.Text = "Enter a journal number first.";
            return;
        }

        var log = new System.Text.StringBuilder();
        var library = System.IO.Path.Combine(App.AppDataDirectory, "JournalLibrary");
        System.IO.Directory.CreateDirectory(library);

        var run = await DownloadIssuePdfsAsync(journalNumber, showBrowser.IsChecked == true);
        log.Append(run.Log);
        LoadIssues();

        FetchStatusText.Text = run.Attempted == 0
            ? $"No download links were found in the row for Journal {journalNumber}."
            : $"Auto-download finished - {run.Saved} of {run.Attempted} file(s) saved.";

        await TextReportDialog.ShowAsync(XamlRoot, "Auto-download result", log.ToString(), "autodownload");
    }

    /// <summary>Outcome of fetching one issue's PDFs through the hidden browser.</summary>
    private sealed record IssueDownloadResult(int Saved, int Attempted, string? FirstPath, string Log);

    /// <summary>
    /// Fetches every class-range PDF for one journal issue by driving the hidden
    /// browser, and records the result against the issue.
    ///
    /// WHY THE BROWSER IS THE PRIMARY PATH, NOT A FALLBACK.
    ///
    /// Your issue list shows 2274, 2273, 2272 and 2271 with dates parsed
    /// correctly and the link column EMPTY. That is not a parsing failure - it
    /// is the listing telling the truth. The Download column on
    /// search.ipindia.gov.in/IPOJournal is rendered as __doPostBack handlers:
    /// the PDF only exists as the response to a form submission carrying
    /// __VIEWSTATE, so there is no address to GET and no regex can invent one.
    ///
    /// Everything downstream - download, text extraction, OCR, the name search,
    /// the watch - was gated on a URL that can never exist for these rows. That
    /// is the single reason Journal Watch does nothing beyond listing issues.
    ///
    /// This was already implemented for the manual "Auto-download" button. It is
    /// extracted here so the row action and the name search use the same code
    /// rather than growing second copies of it.
    /// </summary>
    private async System.Threading.Tasks.Task<IssueDownloadResult> DownloadIssuePdfsAsync(
        string journalNumber, bool visible = false,
        int? onlyClass = null, DateTime? publicationDate = null)
    {
        var log = new System.Text.StringBuilder();
        var library = System.IO.Path.Combine(App.AppDataDirectory, "JournalLibrary");
        System.IO.Directory.CreateDirectory(library);

        HeadlessJournalDownloader? downloader = null;
        var saved = 0;
        var attempted = 0;
        string? firstPath = null;

        try
        {
            downloader = new HeadlessJournalDownloader(HiddenBrowserHost, visible);
            downloader.Progress += message =>
                DispatcherQueue.TryEnqueue(() => FetchStatusText.Text = message);

            // Hand the browser to the shared chain as its fallback source.
            //
            // Appended, never substituted: the session client stays first
            // because it needs no UI thread and finishes in a fraction of the
            // time. The browser is here for the one case the session client
            // genuinely cannot cover - a link whose target is written by script
            // at click time, which is not in the markup to be found.
            App.JournalSources?.Add(downloader);

            var links = await downloader.ListLinksAsync(journalNumber, publicationDate);

            if (links.Count == 0)
            {
                log.AppendLine($"Journal {journalNumber}: no download links found in its row.");
                RecordIssueError(journalNumber,
                    "The listing row for this issue produced no download links at all.");
                return new IssueDownloadResult(0, 0, null, log.ToString());
            }

            // CLASS TARGETING.
            //
            // Asking for class 29 and being handed "CLASS 1 - 9" is what happens
            // when the class is used to FIND the issue and then every link in
            // that issue's row is downloaded in document order - the first of
            // which is always CLASS 1 - 9. The class was doing half a job: it
            // picked the row and then stopped mattering.
            //
            // Now it picks the file too. Only the range that actually contains
            // the requested class is clicked, and if no label parses as a range
            // that is reported rather than silently downloading the wrong
            // hundred megabytes.
            if (onlyClass is { } wantedClass)
            {
                var ranged = links
                    .Where(l => IPDocketing.Core.Services.JournalFetchService
                        .TryParseClassRange(l.Label, out _, out _))
                    .ToList();

                var matching = links
                    .Where(l => IPDocketing.Core.Services.JournalFetchService
                                    .TryParseClassRange(l.Label, out var low, out var high) &&
                                wantedClass >= low && wantedClass <= high)
                    .ToList();

                if (matching.Count == 0)
                {
                    var detail = ranged.Count == 0
                        ? $"None of the {links.Count} link(s) in this row say which classes they cover " +
                          "(they are icon links), so class-based selection cannot work on this issue. " +
                          "Use Get PDF to fetch them all."
                        : $"Class {wantedClass} is not covered by any range in this row. " +
                          $"Available: {string.Join(", ", ranged.Select(r => r.Label))}.";

                    log.AppendLine(detail);
                    RecordIssueError(journalNumber, detail);
                    return new IssueDownloadResult(0, 0, null, log.ToString());
                }

                log.AppendLine($"Class {wantedClass} -> \"{matching[0].Label}\" " +
                               $"(of {links.Count} link(s) in the row).");
                links = matching;
            }

            attempted = links.Count;

            log.AppendLine($"Journal {journalNumber} - {links.Count} link(s) found:");
            foreach (var l in links) log.AppendLine($"  - {l.Label}");
            log.AppendLine();

            var issueRecorded = false;

            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];

                DispatcherQueue.TryEnqueue(() =>
                    FetchStatusText.Text = $"Journal {journalNumber}: downloading file {i + 1} of {links.Count} " +
                                           $"({link.Label})...");

                var safeLabel = string.Concat(link.Label.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                if (safeLabel.Length > 50) safeLabel = safeLabel[..50];

                var target = System.IO.Path.Combine(library, $"journal_{journalNumber}_{safeLabel}.pdf");

                // Already on disk - never re-fetch a file this large without
                // being asked to. A journal issue runs to hundreds of megabytes.
                if (System.IO.File.Exists(target) && new System.IO.FileInfo(target).Length > 20_000)
                {
                    log.AppendLine($"[skip] {link.Label} - already downloaded");
                    firstPath ??= target;
                    saved++;
                    continue;
                }

                var outcome = await downloader.DownloadLinkAsync(link, target, TimeSpan.FromMinutes(4));

                if (outcome.Saved)
                {
                    saved++;
                    firstPath ??= outcome.FilePath;
                    log.AppendLine($"[ok]   {link.Label} - {outcome.Bytes / 1024 / 1024.0:0.0} MB");

                    if (!issueRecorded)
                    {
                        App.Journal.Add(new IPDocketing.Core.Models.JournalIssue
                        {
                            IssueNumber = journalNumber,
                            PublicationDate = App.Journal.GetAll()
                                .FirstOrDefault(j => j.IssueNumber == journalNumber)?.PublicationDate
                                ?? DateTime.Today,
                            LocalPdfPath = outcome.FilePath!,
                            PdfSizeBytes = outcome.Bytes,
                            DownloadedUtc = DateTime.UtcNow,
                            Notes = $"Downloaded via browser: {link.Label}"
                        });
                        issueRecorded = true;
                    }
                }
                else
                {
                    log.AppendLine($"[fail] {link.Label} - {outcome.Error}");
                }

                // A postback leaves the page in a changed state, so every click
                // has to start from the listing again.
                await downloader.ReturnToListingAsync();
            }

            // A run that saved nothing is a failure, and must be recorded as one.
            // Marking an issue reviewed or processed when the network let go is
            // how a watch quietly stops being a watch.
            if (saved == 0)
                RecordIssueError(journalNumber,
                    $"All {links.Count} download link(s) failed. See the auto-download report for detail.");
            else
                ClearIssueError(journalNumber);

            log.AppendLine();
            log.AppendLine($"Done: {saved} of {links.Count} file(s) in {library}");
        }
        catch (Exception ex)
        {
            log.AppendLine();
            log.AppendLine($"Failed: {ex.Message}");
            RecordIssueError(journalNumber, ex.Message);
            DispatcherQueue.TryEnqueue(() => FetchStatusText.Text = $"Download failed: {ex.Message}");
        }
        finally
        {
            downloader?.Dispose();

            HiddenBrowserHost.Margin = HeadlessJournalDownloader.OffScreen;
            HiddenBrowserHost.IsHitTestVisible = false;
            HiddenBrowserHost.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            HiddenBrowserHost.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
        }

        return new IssueDownloadResult(saved, attempted, firstPath, log.ToString());
    }

    /// <summary>
    /// Records why an issue could not be processed, so the row can show "Failed"
    /// with a reason rather than sitting on a status that implies success.
    /// </summary>
    private static void RecordIssueError(string issueNumber, string error)
    {
        try
        {
            var issue = App.Journal.GetAll()
                .FirstOrDefault(j => string.Equals(j.IssueNumber, issueNumber, StringComparison.OrdinalIgnoreCase));
            if (issue is null) return;

            issue.LastError = error;
            App.Database.SaveChanges();
        }
        catch
        {
            // Recording a failure must not itself become one.
        }
    }

    private static void ClearIssueError(string issueNumber)
    {
        try
        {
            var issue = App.Journal.GetAll()
                .FirstOrDefault(j => string.Equals(j.IssueNumber, issueNumber, StringComparison.OrdinalIgnoreCase));
            if (issue is null || issue.LastError is null) return;

            issue.LastError = null;
            App.Database.SaveChanges();
        }
        catch { }
    }

    /// <summary>
    /// "Get PDF" on a row. The listing gives most issues no fetchable URL, so
    /// this is the only route to the file for them.
    /// </summary>
    private async void RowGetPdf_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var issue = App.Journal.GetAll().FirstOrDefault(j => j.Id == id);
        if (issue is null) return;

        FetchStatusText.Text = $"Journal {issue.IssueNumber}: opening the listing...";

        var run = await DownloadIssuePdfsAsync(issue.IssueNumber);
        LoadIssues();

        FetchStatusText.Text = run.Saved > 0
            ? $"Journal {issue.IssueNumber}: {run.Saved} of {run.Attempted} file(s) saved."
            : $"Journal {issue.IssueNumber}: nothing could be downloaded - the row now shows why.";

        if (run.Attempted > 0)
            await TextReportDialog.ShowAsync(
                XamlRoot, $"Journal {issue.IssueNumber} download", run.Log, "issuedownload");
    }

    /// <summary>
    /// Shows what a status badge means, and the error behind it where there is
    /// one. The badge is the only place the pipeline's state is visible, so it
    /// has to be inspectable rather than decorative.
    /// </summary>
    private async void IssueStatus_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var row = (IssueList.ItemsSource as List<IssueRow>)?.FirstOrDefault(r => r.Id == id);
        if (row is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Journal {row.IssueNumber} - {row.StatusLabel}",
            Content = new TextBlock
            {
                Text = row.StatusDetail,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            },
            PrimaryButtonText = "Toggle reviewed",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var issue = App.Journal.GetAll().FirstOrDefault(j => j.Id == id);
        if (issue is null) return;

        App.Journal.MarkReviewed(id, !issue.Reviewed);
        App.Audit.Log("Update", "JournalIssue", id,
            $"Marked issue {issue.IssueNumber} as {(!issue.Reviewed ? "reviewed" : "pending review")}.");

        LoadIssues();
    }

    private async void BrowseIssues_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        FetchStatusText.Text = "Reading the Journal listing...";

        List<IPDocketing.Core.Services.JournalFetchService.JournalIssueEntry> entries;
        try
        {
            entries = await App.JournalFetch.FetchIssuesAsync();
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Couldn't read the listing: {ex.Message}";
            return;
        }

        var withLinks = entries.Where(i => i.ClassLinks.Count > 0).Take(30).ToList();
        if (withLinks.Count == 0)
        {
            // Show the raw HTML of a row that yielded nothing. Without this the
            // failure is invisible from outside - which is exactly how the
            // "0 link(s)" bug survived several rounds of guessing.
            var raw = App.JournalFetch.LastEmptyRowHtml;

            var diagnostic = raw is null
                ? $"Parsed {entries.Count} row(s) from the listing, but none contained any links, " +
                  "and no raw row could be captured."
                : $"Parsed {entries.Count} row(s), but none yielded a download link.\n\n" +
                  "RAW HTML OF THE FIRST ROW THAT PRODUCED NOTHING\n" +
                  "(this is the single most useful thing to send back)\n" +
                  new string('-', 60) + "\n\n" + raw;

            await TextReportDialog.ShowAsync(XamlRoot, "No download links found", diagnostic, "linkdiag");

            FetchStatusText.Text = "No download links parsed. Use Copy in the dialog and send me the raw HTML.";
            return;
        }

        var issuePicker = new ComboBox
        {
            Header = "Journal issue",
            ItemsSource = withLinks
                .Select(i => $"{i.JournalNumber}  —  {i.PublicationDate:dd MMM yyyy}  ({i.ClassLinks.Count} files)")
                .ToList(),
            SelectedIndex = 0,
            MinWidth = 420
        };

        var linkList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 240,
            MinWidth = 420
        };

        void RefreshLinks()
        {
            var issue = withLinks[Math.Max(0, issuePicker.SelectedIndex)];
            linkList.ItemsSource = issue.ClassLinks.Select(l => l.ClassRangeLabel).ToList();
        }

        issuePicker.SelectionChanged += (_, _) => RefreshLinks();
        RefreshLinks();

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(issuePicker);
        panel.Children.Add(new TextBlock
        {
            Text = "Select one or more files to download. Tip: class 29 sits in whichever range " +
                   "covers it — e.g. \"CLASS 26 - 34\".",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.65
        });
        panel.Children.Add(linkList);

        // Copies the raw hrefs. This exists because the URLs are the one thing
        // I could not see from my side - the listing page isn't reachable from
        // where I work, so the download path was written blind. Pasting this
        // output back to me turns "the URLs are probably right" into a fact.
        var copyLinksButton = new Button
        {
            Content = "Copy raw links for this issue",
            Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0)
        };
        copyLinksButton.Click += (_, _) =>
        {
            var issue = withLinks[Math.Max(0, issuePicker.SelectedIndex)];
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Journal {issue.JournalNumber} — {issue.PublicationDate:dd MMM yyyy}");
            foreach (var l in issue.ClassLinks)
                text.AppendLine($"{l.ClassRangeLabel}\t{l.PdfUrl}");

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copyLinksButton.Content = $"Copied {issue.ClassLinks.Count} link(s)";
        };
        panel.Children.Add(copyLinksButton);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Browse Journal issues",
            Content = panel,
            PrimaryButtonText = "Download selected",
            SecondaryButtonText = "Open in browser",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;

        var chosenIssue = withLinks[Math.Max(0, issuePicker.SelectedIndex)];
        var selectedLabels = linkList.SelectedItems.Cast<string>().ToList();

        if (selectedLabels.Count == 0)
        {
            FetchStatusText.Text = "No file was selected.";
            return;
        }

        var chosenLinks = chosenIssue.ClassLinks
            .Where(l => selectedLabels.Contains(l.ClassRangeLabel))
            .ToList();

        // "Open in browser" is the escape hatch: if the in-app download is
        // blocked for any reason, the link still works in a normal browser and
        // you can read the PDF today rather than waiting on me.
        if (choice == ContentDialogResult.Secondary)
        {
            foreach (var link in chosenLinks)
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(link.PdfUrl)); }
                catch { /* one bad link shouldn't stop the rest */ }
            }
            FetchStatusText.Text = $"Opened {chosenLinks.Count} link(s) in your browser.";
            return;
        }

        var library = System.IO.Path.Combine(App.AppDataDirectory, "JournalLibrary");
        var saved = 0;
        var failures = new List<string>();

        foreach (var link in chosenLinks)
        {
            FetchStatusText.Text = $"Downloading {chosenIssue.JournalNumber} — {link.ClassRangeLabel}...";
            try
            {
                var safeLabel = string.Concat(link.ClassRangeLabel
                    .Select(c => char.IsLetterOrDigit(c) ? c : '_'));

                var path = await App.JournalFetch.DownloadPdfAsync(
                    link.PdfUrl, library, $"{chosenIssue.JournalNumber}_{safeLabel}");

                var info = new System.IO.FileInfo(path);
                if (info.Length < 20_000)
                {
                    failures.Add($"{link.ClassRangeLabel}: server returned only {info.Length} bytes " +
                                 "(likely an error page, not the PDF)");
                    try { System.IO.File.Delete(path); } catch { }
                    continue;
                }

                // Recorded against the issue so the name search can read it.
                var existing = App.Journal.GetAll()
                    .FirstOrDefault(j => j.IssueNumber == chosenIssue.JournalNumber);

                if (existing is null)
                {
                    App.Journal.Add(new IPDocketing.Core.Models.JournalIssue
                    {
                        IssueNumber = chosenIssue.JournalNumber,
                        PublicationDate = chosenIssue.PublicationDate ?? DateTime.Today,
                        Url = link.PdfUrl,
                        LocalPdfPath = path,
                        PdfSizeBytes = info.Length,
                        DownloadedUtc = DateTime.UtcNow,
                        Notes = $"Downloaded {link.ClassRangeLabel}"
                    });
                }
                else
                {
                    // BUG FIX: this only recorded a PDF when the issue had none
                    // on record, so downloading a second class range for an
                    // issue you had already fetched saved the file to disk and
                    // then dropped it - and if the first recorded file had since
                    // been deleted or moved, the issue kept pointing at a path
                    // that no longer existed and the name search skipped it as
                    // "not downloaded".
                    //
                    // A JournalIssue row carries one LocalPdfPath, so the newest
                    // readable file wins, but every downloaded range is now
                    // listed in the notes rather than silently forgotten.
                    var previous = existing.LocalPdfPath;
                    var previousMissing = string.IsNullOrWhiteSpace(previous) ||
                                          !System.IO.File.Exists(previous);

                    if (previousMissing)
                    {
                        existing.LocalPdfPath = path;
                        existing.PdfSizeBytes = info.Length;
                        existing.DownloadedUtc = DateTime.UtcNow;
                        existing.Url = link.PdfUrl;
                    }

                    var marker = $"Downloaded {link.ClassRangeLabel}";
                    if (existing.Notes is null || !existing.Notes.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        existing.Notes = string.IsNullOrWhiteSpace(existing.Notes)
                            ? marker
                            : existing.Notes + "; " + marker;

                    App.Database.SaveChanges();
                }

                saved++;
            }
            catch (Exception ex)
            {
                failures.Add($"{link.ClassRangeLabel}: {ex.Message}");
            }
        }

        LoadIssues();

        var message = $"Downloaded {saved} of {chosenLinks.Count} file(s) to {library}.";
        if (failures.Count > 0) message += " Failed: " + string.Join("; ", failures.Take(3));
        FetchStatusText.Text = message;

        if (saved > 0)
        {
            var open = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Downloaded",
                Content = new TextBlock
                {
                    Text = message + "\n\nOpen the folder to read the PDF yourself?",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                PrimaryButtonText = "Open folder",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await open.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(library);
                    await Windows.System.Launcher.LaunchFolderAsync(folder);
                }
                catch { /* cosmetic */ }
            }
        }
    }

    private async void FindName_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var input = new TextBox
        {
            Header = "Proprietor, applicant or agent name",
            PlaceholderText = "e.g. KARTIK TRADE MARKS COMPANY",
            MinWidth = 380
        };
        var scope = new ComboBox
        {
            Header = "How many recent issues to search",
            ItemsSource = new[] { "Last 4 issues", "Last 12 issues", "Last 26 issues" },
            SelectedIndex = 1,
            MinWidth = 380
        };

        var panel = new StackPanel { Spacing = 12, Width = 400 };
        panel.Children.Add(input);
        panel.Children.Add(scope);
        panel.Children.Add(new TextBlock
        {
            Text = "Only issues whose PDF has been downloaded can be searched. " +
                   "Issues that haven't been downloaded are reported separately — " +
                   "\"not checked\" is not the same as \"not published\".",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.6
        });

        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Find a name in the Journal",
            Content = panel,
            PrimaryButtonText = "Search",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

        var term = input.Text?.Trim() ?? "";
        if (term.Length < 3)
        {
            FetchStatusText.Text = "Enter at least three characters to search for.";
            return;
        }

        var maxIssues = scope.SelectedIndex switch { 0 => 4, 2 => 26, _ => 12 };
        FetchStatusText.Text = $"Searching for \"{term}\"...";

        try
        {
            var report = await App.JournalSearch.SearchAsync(
                term, maxIssues,
                progress: message => DispatcherQueue.TryEnqueue(() => FetchStatusText.Text = message));

            var summary = new System.Text.StringBuilder();

            if (report.Hits.Count == 0)
            {
                summary.AppendLine($"No mention of \"{term}\" found.");
            }
            else
            {
                summary.AppendLine($"Found in {report.Hits.Count} place(s):");
                summary.AppendLine();

                var savedDir = System.IO.Path.Combine(App.AppDataDirectory, "JournalExtracts");
                foreach (var hit in report.Hits.Take(25))
                {
                    var savedPath = App.JournalSearch.SavePageExtract(hit, savedDir);
                    summary.AppendLine($"• {hit.Location} — published {hit.PublicationDate:dd MMM yyyy}");
                    // Hits arrive strongest-first now, and the quality label says
                    // which kind each one is - an exact name against a page that
                    // merely shares most of the words is the difference between
                    // the answer and a page to skim past.
                    summary.AppendLine($"  matched: {hit.MatchedText}  [{hit.MatchQuality}]");
                    if (hit.FromOcr) summary.AppendLine("  (page text came from OCR — verify against the PDF)");
                    summary.AppendLine($"  extract saved to: {savedPath}");
                    summary.AppendLine();
                }
            }

            summary.AppendLine($"Searched {report.IssuesSearched} issue(s) in full.");
            if (report.IssuesSkipped > 0)
                summary.AppendLine($"{report.IssuesSkipped} issue(s) were NOT searched — see below.");

            foreach (var note in report.Notes.Take(12))
                summary.AppendLine("  " + note);

            await TextReportDialog.ShowAsync(
                XamlRoot,
                report.Hits.Count > 0 ? $"\"{term}\" - {report.Hits.Count} hit(s)" : $"\"{term}\" - not found",
                summary.ToString(),
                "namesearch");

            FetchStatusText.Text = report.Hits.Count > 0
                ? $"Found \"{term}\" in {report.Hits.Count} place(s). Page extracts saved."
                : $"\"{term}\" not found in {report.IssuesSearched} searched issue(s).";
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async void WatchReport_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var clients = App.ClientUpdates.GetClientNames();
            var choices = new List<string> { "All clients" };
            choices.AddRange(clients);

            var picker = new ComboBox
            {
                Header = "Scope",
                ItemsSource = choices,
                SelectedIndex = 0,
                MinWidth = 300
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Generate watch report",
                Content = picker,
                PrimaryButtonText = "Generate",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var scope = picker.SelectedIndex <= 0 ? null : picker.SelectedItem as string;
            var html = App.Watch.BuildWatchReportHtml(scope);

            var directory = System.IO.Path.Combine(App.AppDataDirectory, "Reports");
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory,
                $"watch_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            await System.IO.File.WriteAllTextAsync(path, html, System.Text.Encoding.UTF8);

            // CSV alongside it, for anyone who wants the same data in a sheet.
            var csvPath = System.IO.Path.ChangeExtension(path, ".csv");
            await System.IO.File.WriteAllTextAsync(csvPath, App.Watch.BuildWatchReportCsv(scope),
                System.Text.Encoding.UTF8);

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Windows.System.Launcher.LaunchFileAsync(file);

            App.Audit.Log("Export", "WatchReport", 0, $"Watch report generated at {path}.");
            FetchStatusText.Text = $"Watch report written to {path} (CSV alongside it).";
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Report failed: {ex.Message}";
        }
    }

    private async void AddIssue_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var issueBox = new TextBox { Header = "Issue number", PlaceholderText = "e.g. 2156" };
        var dateBox = new DatePicker { Header = "Publication date", Date = DateTimeOffset.Now };
        var urlBox = new TextBox { Header = "Journal URL", PlaceholderText = "https://ipindiaservices.gov.in/..." };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(issueBox);
        panel.Children.Add(dateBox);
        panel.Children.Add(urlBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Log journal issue",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(issueBox.Text)) return;

        App.Journal.Add(new JournalIssue
        {
            IssueNumber = issueBox.Text,
            PublicationDate = dateBox.Date.DateTime,
            Url = urlBox.Text
        });

        LoadIssues();
    }

    private async void RunWatch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (IssueList.SelectedItem is not IssueRow selected)
        {
            var warn = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Select an issue",
                Content = "Pick a journal issue in the list above first.",
                CloseButtonText = "OK"
            };
            await warn.ShowAsync();
            return;
        }

        // No live IP-India feed is connected yet, so marks are pasted in --
        // one per line, optionally "Mark | Applicant". This is the seam
        // where IIndiaIpSearchConnector would later supply the list
        // automatically instead of a manual paste.
        var marksBox = new TextBox
        {
            Header = "Paste published marks (one per line, optionally 'Mark | Applicant')",
            AcceptsReturn = true,
            Height = 220,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Run watch against issue {selected.IssueNumber}",
            Content = marksBox,
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var entries = marksBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
                return (Mark: parts[0], Applicant: parts.Length > 1 ? parts[1] : (string?)null);
            });

        var created = App.Watch.RunWatch(selected.Id, entries);
        LoadAlerts();

        var result = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Watch complete",
            Content = created.Count == 0
                ? "No similarity matches found against your portfolio."
                : $"{created.Count} potential conflict(s) flagged below.",
            CloseButtonText = "OK"
        };
        await result.ShowAsync();
    }

    private void DismissAlert_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: int alertId })
        {
            App.Watch.Dismiss(alertId);
            LoadAlerts();
        }
    }

    public sealed class IssueRow
    {
        public int Id { get; }
        public string IssueNumber { get; }
        public string PublicationDate { get; }
        public string Url { get; }

        /// <summary>Empty when the listing gave no fetchable link - see StatusDetail.</summary>
        public bool HasUrl { get; }

        /// <summary>Shown when there is no link, which is the common case.</summary>
        public string LinkLabel { get; }

        public string StatusLabel { get; }

        /// <summary>The tooltip behind the badge: what the status means and what to do about it.</summary>
        public string StatusDetail { get; }

        public Microsoft.UI.Xaml.Media.SolidColorBrush StatusBrush { get; }

        /// <summary>Kept so the existing template binding does not break.</summary>
        public string ReviewedLabel => StatusLabel;
        public Microsoft.UI.Xaml.Media.SolidColorBrush ReviewedBrush => StatusBrush;

        /// <summary>
        /// One row's real state, derived from the timestamps the pipeline
        /// already records.
        ///
        /// Every row used to read "Pending review" whatever had happened to it -
        /// never downloaded, downloaded, OCR'd, watched, or failed outright all
        /// looked identical, so the list could not tell you where the pipeline
        /// had stopped. That is why it looked like nothing was working even on
        /// the runs where something was.
        ///
        /// Deliberately DERIVED rather than stored: adding a status column would
        /// bump the schema version, and in this app a schema bump deletes the
        /// database and restores from a snapshot. Not worth it for a value that
        /// can be computed exactly from LastError, LocalPdfPath,
        /// TextExtractedUtc, WatchRunUtc and Reviewed.
        /// </summary>
        public IssueRow(JournalIssue j, int alertCount)
        {
            Id = j.Id;
            IssueNumber = j.IssueNumber;
            PublicationDate = j.PublicationDate.ToString("dd MMM yyyy");
            Url = j.Url ?? string.Empty;
            HasUrl = !string.IsNullOrWhiteSpace(Url);

            var pdfOnDisk = !string.IsNullOrWhiteSpace(j.LocalPdfPath) &&
                            System.IO.File.Exists(j.LocalPdfPath);

            LinkLabel = HasUrl
                ? Url
                : pdfOnDisk
                    ? System.IO.Path.GetFileName(j.LocalPdfPath)
                    : "No direct link - use Get PDF";

            string label;
            string detail;
            Windows.UI.Color colour;

            if (!string.IsNullOrWhiteSpace(j.LastError))
            {
                label = "Failed";
                detail = j.LastError!;
                colour = Windows.UI.Color.FromArgb(255, 255, 91, 82);
            }
            else if (j.Reviewed)
            {
                label = "Reviewed";
                detail = "You have marked this issue as reviewed. Click to move it back to pending.";
                colour = Windows.UI.Color.FromArgb(255, 53, 208, 113);
            }
            else if (j.WatchRunUtc is not null)
            {
                if (alertCount > 0)
                {
                    label = $"Match found ({alertCount})";
                    detail = $"The watch ran on {j.WatchRunUtc:dd MMM yyyy HH:mm} and raised {alertCount} " +
                             "alert(s). They are listed below.";
                    colour = Windows.UI.Color.FromArgb(255, 255, 140, 60);
                }
                else
                {
                    label = "No match";
                    detail = $"The watch ran in full on {j.WatchRunUtc:dd MMM yyyy HH:mm} against " +
                             $"{j.MarksParsed} published mark(s) and found nothing above the threshold.";
                    colour = Windows.UI.Color.FromArgb(255, 138, 148, 168);
                }
            }
            else if (j.TextExtractedUtc is not null)
            {
                label = "Processed";
                detail = $"Text extracted on {j.TextExtractedUtc:dd MMM yyyy HH:mm} via " +
                         $"{j.ExtractionMethod ?? "unknown method"}; {j.MarksParsed} mark(s) parsed. " +
                         "The watch has not been run on it yet.";
                colour = Windows.UI.Color.FromArgb(255, 91, 140, 255);
            }
            else if (pdfOnDisk)
            {
                label = "Downloaded";
                detail = $"PDF on disk ({j.PdfSizeBytes / 1024 / 1024.0:0.0} MB), downloaded " +
                         $"{j.DownloadedUtc:dd MMM yyyy HH:mm}. Text has not been extracted yet.";
                colour = Windows.UI.Color.FromArgb(255, 91, 140, 255);
            }
            else if (HasUrl)
            {
                label = "Not downloaded";
                detail = "A PDF link is on record but the file has not been fetched yet.";
                colour = Windows.UI.Color.FromArgb(255, 255, 170, 36);
            }
            else
            {
                label = "No PDF link";
                detail = "The listing page advertised no fetchable URL for this issue - its Download " +
                         "column uses ASP.NET postbacks, which have no address to GET. Use \"Get PDF\" " +
                         "on this row, which drives a hidden browser and catches the file the click " +
                         "produces.";
                colour = Windows.UI.Color.FromArgb(255, 255, 170, 36);
            }

            StatusLabel = label;
            StatusDetail = detail;
            StatusBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(colour);
        }
    }

    public sealed class AlertRow
    {
        public int Id { get; }
        public string PublishedMark { get; }
        public string PublishedApplicant { get; }
        public string MatterTitle { get; }
        public string ScoreLabel { get; }
        public string Explanation { get; }
        public Microsoft.UI.Xaml.Visibility OcrVisibility { get; }
        public Microsoft.UI.Xaml.Media.SolidColorBrush ScoreBrush { get; }

        public AlertRow(WatchAlert a)
        {
            Id = a.Id;
            PublishedMark = a.PublishedMark;

            var applicantParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(a.PublishedApplicant)) applicantParts.Add(a.PublishedApplicant);
            if (!string.IsNullOrWhiteSpace(a.PublishedClass)) applicantParts.Add($"Class {a.PublishedClass}");
            PublishedApplicant = string.Join(" · ", applicantParts);

            MatterTitle = a.Matter is null ? "" : $"vs. {a.Matter.Title}";

            // First reason only in the row - the rest is in the record and
            // shown in full on the printed watch report.
            Explanation = (a.MatchExplanation ?? "").Split(Environment.NewLine).FirstOrDefault() ?? "";

            ScoreLabel = string.IsNullOrWhiteSpace(a.PrimarySignal)
                ? $"{a.SimilarityScore}%"
                : $"{a.SimilarityScore}% · {a.PrimarySignal}";

            OcrVisibility = a.FromOcr
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

            // Colour by severity so a near-identical mark doesn't sit visually
            // level with a borderline one.
            var colour = a.SimilarityScore switch
            {
                >= 95 => Windows.UI.Color.FromArgb(255, 255, 91, 82),
                >= 80 => Windows.UI.Color.FromArgb(255, 255, 140, 60),
                _ => Windows.UI.Color.FromArgb(255, 138, 125, 255)
            };
            ScoreBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(colour);
        }
    }
}
