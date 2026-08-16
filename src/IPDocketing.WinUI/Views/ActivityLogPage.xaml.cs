using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

public sealed partial class ActivityLogPage : Page
{
    private List<LogRow> _allRows = new();

    public ActivityLogPage()
    {
        InitializeComponent();
        try { Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ActivityLogPage.Load failed: {ex}"); }
    }

    private void Load()
    {
        // GetRecent caps at a count rather than exposing every row ever
        // written, since the ledger only grows - 500 is generous for
        // reviewing recent activity without pulling an unbounded table.
        _allRows = App.Audit.GetRecent(500).Select(a => new LogRow(a)).ToList();
        LogList.ItemsSource = _allRows;

        var intact = App.Audit.VerifyChainIntegrity();
        IntegrityText.Text = intact ? "Ledger verified intact" : "INTEGRITY CHECK FAILED";
        IntegrityText.Foreground = intact
            ? new SolidColorBrush(Color.FromArgb(255, 53, 208, 113))
            : new SolidColorBrush(Color.FromArgb(255, 255, 91, 82));
    }

    private void Refresh_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Load();

    private void FilterBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        var query = FilterBox.Text?.Trim();
        LogList.ItemsSource = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r =>
                r.EntityLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.ActionType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.Details?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
              ).ToList();
    }

    public sealed class LogRow
    {
        public string Timestamp { get; }
        public string ActionType { get; }
        public string EntityLabel { get; }
        public string? Details { get; }
        public SolidColorBrush ActionBrush { get; }

        public LogRow(UserAction action)
        {
            Timestamp = action.Timestamp.ToLocalTime().ToString("dd MMM yyyy, HH:mm");
            ActionType = action.ActionType;
            EntityLabel = action.EntityId > 0
                ? $"{action.EntityType} #{action.EntityId}"
                : action.EntityType;
            Details = action.Details;

            ActionBrush = action.ActionType switch
            {
                "Create" => Brush(53, 208, 113),
                "Delete" => Brush(255, 91, 82),
                "Update" => Brush(91, 140, 255),
                "Assign" => Brush(138, 125, 255),
                "Complete" => Brush(53, 208, 113),
                _ => Brush(158, 168, 186)
            };
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) =>
            new(Color.FromArgb(255, r, g, b));
    }
}
