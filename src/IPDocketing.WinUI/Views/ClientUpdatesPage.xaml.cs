using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace IPDocketing.WinUI.Views;

public sealed partial class ClientUpdatesPage : Page
{
    private ClientUpdateLog? _current;

    public ClientUpdatesPage()
    {
        InitializeComponent();
        try { ClientList.ItemsSource = App.ClientUpdates.GetClientNames(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ClientUpdatesPage load failed: {ex}"); }
    }

    private void ClientList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClientList.SelectedItem is not string clientName) return;

        var latest = App.ClientUpdates.GetHistory(clientName).FirstOrDefault();
        _current = latest;
        SummaryBox.Text = latest?.SummaryText ?? "No update generated yet for this client.";
        StatusText.Text = latest is null
            ? ""
            : latest.MarkedSent
                ? $"Last generated {latest.GeneratedDate:dd MMM yyyy} — marked sent {latest.SentDate:dd MMM yyyy}"
                : $"Last generated {latest.GeneratedDate:dd MMM yyyy} — not yet marked sent";
    }

    private void Generate_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ClientList.SelectedItem is not string clientName)
        {
            StatusText.Text = "Select a client first.";
            return;
        }

        _current = App.ClientUpdates.GenerateUpdate(clientName);
        SummaryBox.Text = _current.SummaryText;
        StatusText.Text = $"Generated just now. Not yet marked sent.";
    }

    private async void Copy_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryBox.Text)) return;
        var package = new DataPackage();
        package.SetText(SummaryBox.Text);
        Clipboard.SetContent(package);
        StatusText.Text = "Copied to clipboard.";
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void MarkSent_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Generate an update first.";
            return;
        }
        App.ClientUpdates.MarkSent(_current.Id);
        StatusText.Text = $"Marked sent {DateTime.Now:dd MMM yyyy}.";
    }
}
