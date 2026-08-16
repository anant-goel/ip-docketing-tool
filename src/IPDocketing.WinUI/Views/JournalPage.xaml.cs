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
            PublicationDate = dateBox.Date?.DateTime ?? DateTime.UtcNow,
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

        public AlertRow(WatchAlert a)
        {
            Id = a.Id;
            PublishedMark = a.PublishedMark;
            PublishedApplicant = a.PublishedApplicant ?? "";
            MatterTitle = a.Matter is null ? "" : $"vs. {a.Matter.Title}";
            ScoreLabel = $"{a.SimilarityScore}% match";
        }
    }
}
