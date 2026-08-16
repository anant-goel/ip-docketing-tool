using IPDocketing.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class PtoSyncPage : Page
{
    private readonly List<string> _activity = new();

    public PtoSyncPage()
    {
        InitializeComponent();
        try
        {
            SourceCombo.ItemsSource = Enum.GetValues<PtoSource>();
            SourceCombo.SelectedIndex = 0;
            AddActivity("PTO synchronization is ready for a configured connection.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PtoSyncPage init failed: {ex}");
        }
    }

    private PtoSource SelectedSource => SourceCombo.SelectedItem is PtoSource source
        ? source
        : PtoSource.USPTO;

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = $"{SelectedSource}: credentials required";
        AddActivity($"Connection requested for {SelectedSource}. Add office credentials before live sync.");
    }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        AddActivity($"Sync requested for {SelectedSource}; no live connection is configured.");
    }

    private void AddActivity(string message)
    {
        _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        ActivityList.ItemsSource = null;
        ActivityList.ItemsSource = _activity;
    }
}
