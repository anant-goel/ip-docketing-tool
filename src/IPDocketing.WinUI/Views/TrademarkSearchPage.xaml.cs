using System.IO;
using System.Text;
using IPDocketing.Core.Models;
using IPDocketing.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace IPDocketing.WinUI.Views;

/// <summary>
/// docx section 6 - comprehensive trademark search.
///
/// Scope, stated up front so it is never mistaken for something it isn't: this
/// searches the marks recorded in THIS docket. It is not a register search.
/// Searching the IP India register itself needs a session against
/// tmrsearch.ipindia.gov.in behind a CAPTCHA that a human has to solve, which
/// is what the IP India Portal page (embedded browser) is for.
///
/// All four match modes from the spec are here, plus proprietor / attorney /
/// state, plus class, plus the two result filters the spec asks for (status of
/// the mark, and any alert reflected on the status page).
/// </summary>
public sealed partial class TrademarkSearchPage : Page
{
    private List<ResultRow> _results = new();
    private bool _initializing = true;

    public TrademarkSearchPage()
    {
        InitializeComponent();
        try { PopulateFilters(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TrademarkSearchPage init failed: {ex}"); }
        _initializing = false;
    }

    private void PopulateFilters()
    {
        var statuses = new List<string> { "Any status" };
        statuses.AddRange(Enum.GetNames<MatterStatus>());
        StatusFilterBox.ItemsSource = statuses;
        StatusFilterBox.SelectedIndex = 0;

        var alerts = new List<string> { "Any alert" };
        alerts.AddRange(App.Matters.GetKnownAlerts());
        AlertFilterBox.ItemsSource = alerts;
        AlertFilterBox.SelectedIndex = 0;

        if (alerts.Count == 1)
            FilterNoteText.Text = "No alerts recorded yet. Add one on a matter (Matters → Edit → Portal alert) " +
                                  "to filter on it here, e.g. \"Opposed\" or \"Objected\".";
    }

    private void MarkBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) RunSearch();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        // Re-running on filter change is cheap and matches what the spec asks
        // for ("results should also be filtered based on...") - the filter acts
        // on the result set, not as a separate query the user has to re-submit.
        if (_initializing) return;
        RunSearch();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        MarkBox.Text = string.Empty;
        ProprietorBox.Text = string.Empty;
        AttorneyBox.Text = string.Empty;
        StateBox.Text = string.Empty;
        ClassBox.Text = string.Empty;
        ModeBox.SelectedIndex = 0;
        MarkTypeBox.SelectedIndex = 0;
        StatusFilterBox.SelectedIndex = 0;
        AlertFilterBox.SelectedIndex = 0;
        OnlyAlertsCheck.IsChecked = false;
        _initializing = false;

        _results = new List<ResultRow>();
        ResultList.ItemsSource = _results;
        ResultCountText.Text = "0 results";
        EmptyTitleText.Text = "Search the portfolio";
        EmptyBodyText.Text = "Enter a mark, or leave it blank and press Search to list everything, then narrow with the filters.";
        EmptyState.Visibility = Visibility.Visible;
    }

    private void RunSearch()
    {
        try
        {
            var query = new MatterService.MarkSearchQuery
            {
                Mark = MarkBox.Text,
                Mode = SelectedMode(),
                Proprietor = ProprietorBox.Text,
                Attorney = AttorneyBox.Text,
                State = StateBox.Text,
                NiceClass = ClassBox.Text,
                MarkType = SelectedMarkType(),
                Status = SelectedStatus(),
                Alert = SelectedAlert(),
                OnlyWithAlerts = OnlyAlertsCheck.IsChecked == true,
                TrademarksOnly = true
            };

            _results = App.Matters.Search(query).Select(m => new ResultRow(m)).ToList();
            ResultList.ItemsSource = _results;
            ResultCountText.Text = _results.Count == 1 ? "1 result" : $"{_results.Count} results";

            if (_results.Count == 0)
            {
                EmptyTitleText.Text = "No marks matched";
                EmptyBodyText.Text = query.Mode == MatterService.MarkMatchMode.Phonetic
                    ? "Phonetic matching buckets marks by sound, so it only finds close-sounding names. Try Contains for a broader sweep."
                    : "Nothing in the docket matches those terms. Widen the match mode, clear a filter, or check the mark is on file.";
                EmptyState.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            EmptyTitleText.Text = "Search failed";
            EmptyBodyText.Text = ex.Message;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private MatterService.MarkMatchMode SelectedMode() =>
        ((ModeBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Exact" => MatterService.MarkMatchMode.Exact,
            "StartsWith" => MatterService.MarkMatchMode.StartsWith,
            "Phonetic" => MatterService.MarkMatchMode.Phonetic,
            _ => MatterService.MarkMatchMode.Contains
        };

    private MarkType? SelectedMarkType() =>
        ((MarkTypeBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Word" => MarkType.Word,
            "Device" => MarkType.Device,
            _ => null
        };

    private MatterStatus? SelectedStatus()
    {
        if (StatusFilterBox.SelectedItem is not string text || text == "Any status") return null;
        return Enum.TryParse<MatterStatus>(text, out var status) ? status : null;
    }

    private string? SelectedAlert()
    {
        if (AlertFilterBox.SelectedItem is not string text || text == "Any alert") return null;
        return text;
    }

    private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // A double-click on a result goes straight to that mark's full history,
        // which is the next thing anyone wants after finding it.
        if (ResultList.SelectedItem is not ResultRow row) return;
        Frame?.Navigate(typeof(StatusTrackerPage), row.Id);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            await Notify("Nothing to export", "Run a search first — the export writes the current result set.");
            return;
        }

        try
        {
            var csv = new StringBuilder(
                "MatterNumber,ApplicationNumber,Mark,MarkType,Class,Proprietor,Attorney,State,Status,PortalAlert,Client,FilingDate\n");

            foreach (var row in _results)
            {
                var m = row.Source;
                csv.AppendLine(string.Join(',',
                    Csv(m.MatterNumber), Csv(m.ApplicationNumber), Csv(m.Title),
                    Csv(m.MarkType?.ToString()), Csv(m.NiceClass), Csv(m.ProprietorName),
                    Csv(m.AttorneyOfRecord), Csv(m.State), Csv(m.Status.ToString()),
                    Csv(m.PortalAlert), Csv(m.ClientName),
                    Csv(m.FilingDate?.ToString("yyyy-MM-dd"))));
            }

            var directory = Path.Combine(App.AppDataDirectory, "Exports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"tm_search_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            await File.WriteAllTextAsync(path, csv.ToString(), Encoding.UTF8);

            App.Audit.Log("Export", "Search", 0, $"Exported {_results.Count} search result(s) to {path}.");
            await Notify("Exported", $"{_results.Count} result(s) written to:\n{path}");
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
            Content = content,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    public sealed class ResultRow
    {
        public Matter Source { get; }
        public int Id { get; }
        public string Title { get; }
        public string Identity { get; }
        public string Proprietor { get; }
        public string Attorney { get; }
        public string State { get; }
        public string ClassLabel { get; }
        public string Status { get; }
        public string Alert { get; }
        public string MarkTypeGlyph { get; }
        public Visibility AlertVisibility { get; }

        public ResultRow(Matter m)
        {
            Source = m;
            Id = m.Id;
            Title = m.Title;
            Identity = string.IsNullOrWhiteSpace(m.ApplicationNumber)
                ? $"{m.MatterNumber} · no application number"
                : $"{m.MatterNumber} · App# {m.ApplicationNumber}";
            Proprietor = m.ProprietorName ?? "Proprietor not recorded";
            Attorney = string.IsNullOrWhiteSpace(m.AttorneyOfRecord) ? "" : $"Attorney: {m.AttorneyOfRecord}";
            State = m.State ?? "-";
            ClassLabel = string.IsNullOrWhiteSpace(m.NiceClass) ? "Class not set" : $"Class {m.NiceClass}";
            Status = m.Status.ToString();
            Alert = m.PortalAlert ?? "";
            AlertVisibility = string.IsNullOrWhiteSpace(m.PortalAlert) ? Visibility.Collapsed : Visibility.Visible;
            MarkTypeGlyph = m.MarkType == MarkType.Device ? "DEV" : "WORD";
        }
    }
}
