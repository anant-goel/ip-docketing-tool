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

    /// <summary>
    /// docx section 8. The drafting really is automatic - this button just runs
    /// it on demand for everyone rather than waiting for the weekly startup
    /// pass. Sending still needs a person, because nothing in this app can put
    /// mail on the wire.
    /// </summary>
    private async void GenerateAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var generated = App.ClientUpdates.GenerateForAllClients();
            ClientList.ItemsSource = App.ClientUpdates.GetClientNames();

            StatusText.Text = generated.Count == 0
                ? "No clients on file yet - add a matter with a client name first."
                : $"Drafted {generated.Count} update(s). Select a client to review before sending.";

            App.Audit.Log("Generate", "ClientUpdate", 0,
                $"Bulk-generated {generated.Count} client update(s).");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Generation failed: {ex.Message}";
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Opens the default mail client with the draft already filled in. The
    /// recipient is left blank on purpose - client contact addresses aren't
    /// stored anywhere in this schema, and there's no good reason for a local
    /// docketing tool to start holding client PII.
    /// </summary>
    private async void OpenMail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Generate an update first.";
            return;
        }

        try
        {
            var uri = new Uri(App.ClientUpdates.BuildMailtoUri(_current));
            if (await Windows.System.Launcher.LaunchUriAsync(uri))
            {
                StatusText.Text = "Opened in your mail app. Add the recipient and send.";
            }
            else
            {
                StatusText.Text = "Windows has no default mail client configured - copy the text instead.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open a mail draft: {ex.Message}";
        }
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
