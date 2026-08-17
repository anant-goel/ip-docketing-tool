using System.IO;
using System.Text;
using IPDocketing.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

/// <summary>
/// The renewal watchlist. Renewals are the one deadline nobody else reminds you
/// about - the Registry's s.25(3) notice goes to the proprietor's address on
/// record, which for an agent-filed mark is very often stale - so this view
/// deliberately keeps lapsed and restoration-stage marks visible rather than
/// filtering them away once they go past due.
/// </summary>
public sealed partial class RenewalsPage : Page
{
    private List<RenewalService.RenewalRow> _rows = new();
    private bool _initializing = true;

    public RenewalsPage()
    {
        InitializeComponent();
        try
        {
            PopulateAttorneys();
            Load();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RenewalsPage init failed: {ex}");
        }
        _initializing = false;
    }

    private void PopulateAttorneys()
    {
        var names = new List<string> { "Any attorney" };
        names.AddRange(App.Matters.GetAll()
            .Select(m => m.AttorneyOfRecord)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));

        AttorneyBox.ItemsSource = names;
        AttorneyBox.SelectedIndex = 0;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        Load();
    }

    private void Load()
    {
        var horizon = int.TryParse((HorizonBox.SelectedItem as ComboBoxItem)?.Tag as string, out var days)
            ? days
            : 400;

        _rows = App.Renewals.GetWatchlist(horizon);

        if (AttorneyBox.SelectedIndex > 0 && AttorneyBox.SelectedItem is string attorney)
            _rows = _rows.Where(r => string.Equals(r.AttorneyOfRecord, attorney, StringComparison.OrdinalIgnoreCase)).ToList();

        RenewalList.ItemsSource = _rows.Select(r => new RenewalRowView(r)).ToList();
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var overdue = _rows.Count(r => r.DaysRemaining < 0);
        var soon = _rows.Count(r => r.DaysRemaining is >= 0 and <= 90);
        SummaryText.Text = _rows.Count == 0
            ? "Nothing scheduled in this window."
            : $"{_rows.Count} mark(s) — {overdue} past expiry, {soon} due within 90 days.";
    }

    private async void Redocket_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = App.Renewals.DocketRenewals();
            Load();

            var message = new StringBuilder();
            message.AppendLine($"Checked {result.MattersProcessed} trademark(s).");
            message.AppendLine($"Created {result.DeadlinesCreated} new renewal deadline(s).");
            if (result.Skipped > 0)
                message.AppendLine($"Skipped {result.Skipped} with no date to anchor a term on.");

            if (result.Notes.Count > 0)
            {
                message.AppendLine();
                foreach (var note in result.Notes.Take(15)) message.AppendLine("• " + note);
                if (result.Notes.Count > 15) message.AppendLine($"...and {result.Notes.Count - 15} more.");
            }

            await Notify("Renewal docketing complete", message.ToString());
        }
        catch (Exception ex)
        {
            await Notify("Docketing failed", ex.Message);
        }
    }

    private async void MarkRenewed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int matterId }) return;

        var matter = App.Matters.GetById(matterId);
        if (matter is null) return;

        var datePicker = new CalendarDatePicker
        {
            Header = "Renewal filed / paid on",
            Date = DateTimeOffset.Now,
            MinWidth = 300
        };

        var panel = new StackPanel { Spacing = 12, Width = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{matter.MatterNumber} — {matter.Title}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        panel.Children.Add(datePicker);
        panel.Children.Add(new TextBlock
        {
            Text = "The next ten-year term runs from the previous expiry date, not from today — " +
                   "renewing early doesn't shorten the new term.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.6
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Record renewal",
            Content = panel,
            PrimaryButtonText = "Record",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        App.Renewals.RecordRenewal(matterId, datePicker.Date?.DateTime ?? DateTime.Today);
        Load();
    }

    private void OpenStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int matterId })
            Frame?.Navigate(typeof(StatusTrackerPage), matterId);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            await Notify("Nothing to export", "The current watchlist is empty.");
            return;
        }

        try
        {
            var csv = new StringBuilder("MatterNumber,Mark,Client,Attorney,ExpiryDate,DaysRemaining,Stage\n");
            foreach (var r in _rows)
                csv.AppendLine(string.Join(',',
                    Csv(r.MatterNumber), Csv(r.Title), Csv(r.ClientName), Csv(r.AttorneyOfRecord),
                    Csv(r.ExpiryDate.ToString("yyyy-MM-dd")), r.DaysRemaining, Csv(r.Stage)));

            var directory = Path.Combine(App.AppDataDirectory, "Exports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"renewals_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            await File.WriteAllTextAsync(path, csv.ToString(), Encoding.UTF8);

            await Notify("Exported", $"{_rows.Count} row(s) written to:\n{path}");
        }
        catch (Exception ex)
        {
            await Notify("Export failed", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task Notify(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                MaxHeight = 400
            },
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    public sealed class RenewalRowView
    {
        public int MatterId { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string ExpiryLabel { get; }
        public string CountdownLabel { get; }
        public string Attorney { get; }
        public string Stage { get; }
        public SolidColorBrush StageBrush { get; }

        public RenewalRowView(RenewalService.RenewalRow row)
        {
            MatterId = row.MatterId;
            Title = row.Title;
            Subtitle = $"{row.MatterNumber} · {row.ClientName}";
            ExpiryLabel = row.ExpiryDate.ToString("dd MMM yyyy");
            Attorney = string.IsNullOrWhiteSpace(row.AttorneyOfRecord) ? "—" : row.AttorneyOfRecord;
            Stage = row.Stage;

            CountdownLabel = row.DaysRemaining switch
            {
                < 0 => $"{Math.Abs(row.DaysRemaining)} day(s) past expiry",
                0 => "expires today",
                1 => "1 day left",
                _ => $"{row.DaysRemaining} days left"
            };

            StageBrush = row.Stage switch
            {
                "Lapsed — beyond restoration" => Brush(153, 160, 179),
                "Restoration only" => Brush(255, 91, 82),
                "Late — surcharge payable" => Brush(255, 140, 60),
                "Renewable now" => Brush(255, 170, 36),
                _ => Brush(53, 208, 113)
            };
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    }
}
