using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        RefreshBackupUi();
    }

    private void RefreshBackupUi()
    {
        BackupStatusText.Text = App.Backups.LastStatus;
        BackupFolderText.Text = "Folder: " + App.Backups.BackupDirectory;
    }

    private void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        App.Backups.BackupNow("manual");
        RefreshBackupUi();
        App.Audit.Log("Settings", "Backup", 0, "Manual encrypted backup (WinUI)");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.Backups.BackupDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = App.Backups.BackupDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "Could not open folder: " + ex.Message;
        }
    }

    private void VerifyChain_Click(object sender, RoutedEventArgs e)
    {
        var ok = App.Audit.VerifyChainIntegrity();
        ChainStatusText.Text = ok
            ? $"Verified OK at {DateTime.Now:g}"
            : "INTEGRITY FAILURE — chain may have been altered.";
    }
}
