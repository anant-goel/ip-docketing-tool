using System.Diagnostics;
using System.Text.Json;
using IPDocketing.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly string KeysEncPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPDocketing", "api-keys.enc");

    public SettingsPage()
    {
        InitializeComponent();
        LoadApiKeys();
        RefreshBackupUi();
    }

    private void RefreshBackupUi()
    {
        BackupStatusText.Text = App.Backups.LastStatus;
        BackupFolderText.Text = "Folder: " + App.Backups.BackupDirectory;
        BackupList.ItemsSource = App.Backups.ListBackups()
            .Select(Path.GetFileName)
            .Take(15)
            .ToList();
    }

    private void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        App.Backups.BackupNow("manual");
        RefreshBackupUi();
        App.Audit.Log("Settings", "Backup", 0, "Manual encrypted backup (WinUI)");
    }

    private void RefreshBackups_Click(object sender, RoutedEventArgs e) => RefreshBackupUi();

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

    private void LoadApiKeys()
    {
        try
        {
            var json = EncryptionService.DecryptStringFromFile(KeysEncPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var data = JsonSerializer.Deserialize<ApiKeysConfig>(json);
            if (data is null) return;
            UsptoClientIdBox.Text = data.UsptoClientId ?? "";
            UsptoApiKeyBox.Text = data.UsptoApiKey ?? "";
            EpoKeyBox.Text = data.EpoConsumerKey ?? "";
            EpoSecretBox.Text = data.EpoConsumerSecret ?? "";
            WipoKeyBox.Text = data.WipoApiKey ?? "";
            OcrKeyBox.Text = data.OcrApiKey ?? "";
            if (!string.IsNullOrWhiteSpace(data.OcrProvider))
            {
                for (int i = 0; i < OcrProviderBox.Items.Count; i++)
                {
                    if (OcrProviderBox.Items[i]?.ToString() == data.OcrProvider)
                    {
                        OcrProviderBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
        catch
        {
            ApiKeysStatusText.Text = "Could not load API keys (wrong user or missing file).";
        }
    }

    private void SaveApiKeys_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = new ApiKeysConfig
            {
                UsptoClientId = UsptoClientIdBox.Text,
                UsptoApiKey = UsptoApiKeyBox.Text,
                EpoConsumerKey = EpoKeyBox.Text,
                EpoConsumerSecret = EpoSecretBox.Text,
                WipoApiKey = WipoKeyBox.Text,
                OcrApiKey = OcrKeyBox.Text,
                OcrProvider = OcrProviderBox.SelectedItem?.ToString()
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            EncryptionService.EncryptStringToFile(json, KeysEncPath);
            ApiKeysStatusText.Text = $"Encrypted & saved at {DateTime.Now:g}";
            App.Audit.Log("Settings", "ApiKeys", 0, "API keys updated (encrypted; values not logged)");
        }
        catch (Exception ex)
        {
            ApiKeysStatusText.Text = "Save failed: " + ex.Message;
        }
    }

    private void ClearApiKeys_Click(object sender, RoutedEventArgs e)
    {
        UsptoClientIdBox.Text = UsptoApiKeyBox.Text = "";
        EpoKeyBox.Text = EpoSecretBox.Text = "";
        WipoKeyBox.Text = OcrKeyBox.Text = "";
        OcrProviderBox.SelectedIndex = 0;
        try { if (File.Exists(KeysEncPath)) File.Delete(KeysEncPath); } catch { /* ignore */ }
        ApiKeysStatusText.Text = "API keys cleared.";
    }

    private sealed class ApiKeysConfig
    {
        public string? UsptoApiKey { get; set; }
        public string? UsptoClientId { get; set; }
        public string? EpoConsumerKey { get; set; }
        public string? EpoConsumerSecret { get; set; }
        public string? WipoApiKey { get; set; }
        public string? OcrApiKey { get; set; }
        public string? OcrProvider { get; set; }
    }
}
