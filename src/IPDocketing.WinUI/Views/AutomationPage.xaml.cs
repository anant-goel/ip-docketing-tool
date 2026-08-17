using IPDocketing.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

/// <summary>
/// Control surface for the unattended Journal pipeline.
///
/// The three-card split at the top of this page is deliberate and is not
/// decoration. Someone who believes their register data is refreshing on its
/// own, when in fact only the Journal side is, will miss a status change that
/// matters. Saying plainly which parts run alone - and which will never - is
/// part of the tool being trustworthy.
/// </summary>
public sealed partial class AutomationPage : Page
{
    private bool _initializing = true;

    public AutomationPage()
    {
        InitializeComponent();

        try
        {
            AutoSyncToggle.IsOn = App.AutoSyncEnabled;
            App.AutoSync.Progress += OnProgress;
            Unloaded += (_, _) => App.AutoSync.Progress -= OnProgress;

            RefreshStatus();
            LoadIssues();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutomationPage init failed: {ex}");
        }

        _initializing = false;
    }

    private void OnProgress(string message)
    {
        // Progress arrives from the sync's own thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = message;
            SyncProgress.Visibility = App.AutoSync.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            RunNowButton.IsEnabled = !App.AutoSync.IsRunning;
        });
    }

    private void RefreshStatus()
    {
        StatusText.Text = App.AutoSync.LastStatus;
        SyncProgress.Visibility = App.AutoSync.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        RunNowButton.IsEnabled = !App.AutoSync.IsRunning;

        LastRunText.Text = App.AutoSync.LastRunUtc is { } run
            ? $"Last checked {run.ToLocalTime():dd MMM yyyy HH:mm}."
            : "Not checked yet in this session.";
    }

    private void LoadIssues()
    {
        var issues = App.Journal.GetAll()
            .OrderByDescending(j => j.PublicationDate)
            .Take(40)
            .Select(j => new IssueRow(j))
            .ToList();

        IssueList.ItemsSource = issues;
        EmptyState.Visibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        RunNowButton.IsEnabled = false;
        SyncProgress.Visibility = Visibility.Visible;

        try
        {
            var report = await App.AutoSync.RunOnceAsync();
            LoadIssues();
            RefreshStatus();

            if (report.Notes.Count > 0)
            {
                var text = string.Join(Environment.NewLine + Environment.NewLine, report.Notes.Take(12));
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Sync finished with notes",
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                        MaxHeight = 380
                    },
                    CloseButtonText = "OK"
                };
                await dialog.ShowAsync();
            }
        }
        finally
        {
            RunNowButton.IsEnabled = true;
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoSyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        App.SetAutoSyncEnabled(AutoSyncToggle.IsOn);
        StatusText.Text = AutoSyncToggle.IsOn
            ? $"Automatic checking on — every {App.AutoSync.Interval.TotalHours:0} hour(s) while the app is open."
            : "Automatic checking off. Use Run now when you want a pass.";
    }

    private void Interval_Changed(object sender, object e)
    {
        if (_initializing) return;

        if (int.TryParse((IntervalBox.SelectedItem as ComboBoxItem)?.Tag as string, out var hours))
            App.AutoSync.Interval = TimeSpan.FromHours(hours);

        if (int.TryParse((BatchBox.SelectedItem as ComboBoxItem)?.Tag as string, out var batch))
            App.AutoSync.MaxDownloadsPerRun = batch;

        // Restarting is what actually applies a new interval to the timer.
        if (AutoSyncToggle.IsOn) App.SetAutoSyncEnabled(true);
    }

    private void OpenPortal_Click(object sender, RoutedEventArgs e) =>
        Frame?.Navigate(typeof(IpIndiaPortalPage));

    private async void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(App.AppDataDirectory, "JournalLibrary");
            System.IO.Directory.CreateDirectory(path);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open the library folder: {ex.Message}";
        }
    }

    public sealed class IssueRow
    {
        public string IssueNumber { get; }
        public string PublishedLabel { get; }
        public string Detail { get; }
        public string Stage { get; }
        public SolidColorBrush StageBrush { get; }

        public IssueRow(JournalIssue issue)
        {
            IssueNumber = $"Journal {issue.IssueNumber}";
            PublishedLabel = issue.PublicationDate.ToString("dd MMM yyyy");

            if (issue.LastError is { Length: > 0 })
            {
                Stage = "Problem";
                StageBrush = Brush(255, 91, 82);
                Detail = issue.LastError;
            }
            else if (issue.WatchRunUtc is not null)
            {
                Stage = "Watched";
                StageBrush = Brush(53, 208, 113);
                Detail = issue.Notes ?? $"{issue.MarksParsed} mark(s) parsed.";
            }
            else if (issue.TextExtractedUtc is not null)
            {
                Stage = "Read";
                StageBrush = Brush(138, 125, 255);
                Detail = $"{issue.MarksParsed} mark(s) parsed via {issue.ExtractionMethod ?? "text"}; watch pending.";
            }
            else if (issue.LocalPdfPath is not null)
            {
                Stage = "Downloaded";
                StageBrush = Brush(90, 170, 255);
                Detail = $"{issue.PdfSizeBytes / 1024 / 1024.0:0.0} MB on disk; waiting to be read.";
            }
            else
            {
                Stage = "Queued";
                StageBrush = Brush(255, 170, 36);
                Detail = string.IsNullOrWhiteSpace(issue.Url)
                    ? "No PDF link was advertised for this issue - nothing to download."
                    : "Waiting for the next download pass.";
            }
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    }
}
