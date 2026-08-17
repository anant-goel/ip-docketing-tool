using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class JournalPage : Page
{
    public JournalPage()
    {
        InitializeComponent();
        try { LoadIssues(); LoadAlerts(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"JournalPage load failed: {ex}"); }
    }

    private void LoadIssues()
    {
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

        var date = FetchDateBox.Date.DateTime;
        FetchStatusText.Text = "Fetching from IP India...";

        try
        {
            var result = await App.JournalFetch.FindByDateAndClassAsync(date, trademarkClass);
            if (result is null)
            {
                FetchStatusText.Text = $"No journal issue found on/before {date:dd MMM yyyy}, or class {trademarkClass} " +
                                        "wasn't in a parseable range for that issue (some rows are notices/well-known-marks only).";
                return;
            }

            var (issue, classRange, pdfUrl) = result.Value;

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
        var dateBox = new DatePicker { Header = "Publication date" };
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

        public IssueRow(JournalIssue j)
        {
            Id = j.Id;
            IssueNumber = j.IssueNumber;
            PublicationDate = j.PublicationDate.ToString("dd MMM yyyy");
            Url = j.Url;
            ReviewedLabel = j.Reviewed ? "Reviewed" : "Pending review";
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
