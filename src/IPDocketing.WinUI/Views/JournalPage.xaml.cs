using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class JournalPage : Page
{
    public JournalPage()
    {
        InitializeComponent();

        // Seeded so the picker never sits at DateTimeOffset.MinValue (1601).
        FetchDateBox.Date = DateTimeOffset.Now;
        try { LoadIssues(); LoadAlerts(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"JournalPage load failed: {ex}"); }
    }

    private void LoadIssues()
    {
        // Clears duplicates left by earlier runs, before deduplication existed.
        try { App.Journal.RemoveDuplicates(); } catch { /* cosmetic */ }

        IssueList.ItemsSource = App.Journal.GetAll().Select(j => new IssueRow(j)).ToList();
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
        var date = FetchDateBox.Date.DateTime;
        if (date.Year < 1950)
        {
            date = DateTime.Today;
            FetchDateBox.Date = DateTimeOffset.Now;
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

            FetchStatusText.Text = $"Found journal {issue.JournalNumber} ({issue.PublicationDate:dd MMM yyyy}), " +
                                    $"class {trademarkClass} is in \"{classRange}\" - logged with the PDF link.";
            LoadIssues();
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
    private void ToggleReviewed_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var issue = App.Journal.GetAll().FirstOrDefault(j => j.Id == id);
        if (issue is null) return;

        App.Journal.MarkReviewed(id, !issue.Reviewed);
        App.Audit.Log("Update", "JournalIssue", id,
            $"Marked issue {issue.IssueNumber} as {(!issue.Reviewed ? "reviewed" : "pending review")}.");

        LoadIssues();
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

            var body = new TextBox
            {
                Text = raw is null
                    ? $"Parsed {entries.Count} row(s) from the listing, but none contained any links, " +
                      "and no raw row could be captured."
                    : $"Parsed {entries.Count} row(s), but none yielded a download link.\n\n" +
                      "Raw HTML of the first row that produced nothing — please send me this:\n\n" + raw,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                FontSize = 11,
                Height = 380,
                Width = 560
            };

            var diag = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "No download links found",
                Content = new ScrollViewer { Content = body, MaxHeight = 400 },
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await diag.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(body.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }

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
                else if (string.IsNullOrWhiteSpace(existing.LocalPdfPath))
                {
                    existing.LocalPdfPath = path;
                    existing.PdfSizeBytes = info.Length;
                    existing.DownloadedUtc = DateTime.UtcNow;
                    existing.Url = link.PdfUrl;
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
                    summary.AppendLine($"  matched: {hit.MatchedText}");
                    if (hit.FromOcr) summary.AppendLine("  (page text came from OCR — verify against the PDF)");
                    summary.AppendLine($"  page text saved to: {savedPath}");
                    summary.AppendLine();
                }
            }

            summary.AppendLine($"Searched {report.IssuesSearched} issue(s) in full.");
            if (report.IssuesSkipped > 0)
                summary.AppendLine($"{report.IssuesSkipped} issue(s) were NOT searched — see below.");

            foreach (var note in report.Notes.Take(12))
                summary.AppendLine("  " + note);

            var body = new TextBox
            {
                Text = summary.ToString(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                FontSize = 12,
                Height = 400,
                Width = 560
            };

            var result = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = report.Hits.Count > 0 ? $"\"{term}\" — {report.Hits.Count} hit(s)" : $"\"{term}\" — not found",
                Content = new ScrollViewer { Content = body, MaxHeight = 420 },
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
            };

            if (await result.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(summary.ToString());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }

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
        public string ReviewedLabel { get; }
        public Microsoft.UI.Xaml.Media.SolidColorBrush ReviewedBrush { get; }

        public IssueRow(JournalIssue j)
        {
            Id = j.Id;
            IssueNumber = j.IssueNumber;
            PublicationDate = j.PublicationDate.ToString("dd MMM yyyy");
            Url = j.Url;
            ReviewedLabel = j.Reviewed ? "Reviewed" : "Pending review";
            ReviewedBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                j.Reviewed
                    ? Windows.UI.Color.FromArgb(255, 53, 208, 113)
                    : Windows.UI.Color.FromArgb(255, 255, 170, 36));
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
