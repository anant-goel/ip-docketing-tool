using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPDocketing.Core.Models;
using IPDocketing.Core.Services;

namespace IPDocketing.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private static readonly string KeysEncPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPDocketing", "api-keys.enc");

    [ObservableProperty]
    private bool isDarkMode = true;

    [ObservableProperty]
    private string currentUser = "local.user";

    [ObservableProperty]
    private string currentRole = "Attorney";

    [ObservableProperty]
    private string chainStatus = "Not yet verified";

    // ---- API Keys ----
    [ObservableProperty]
    private string usptoApiKey = "";

    [ObservableProperty]
    private string usptoClientId = "";

    [ObservableProperty]
    private string epoConsumerKey = "";

    [ObservableProperty]
    private string epoConsumerSecret = "";

    [ObservableProperty]
    private string wipoApiKey = "";

    [ObservableProperty]
    private string ocrApiKey = "";

    [ObservableProperty]
    private string ocrProvider = "None (local / Tesseract)";

    [ObservableProperty]
    private string apiKeysStatus = "";

    // ---- Backup ----
    [ObservableProperty]
    private string backupStatus = "";

    [ObservableProperty]
    private string backupFolder = "";

    public ObservableCollection<string> Roles { get; } = new()
    {
        "Administrator", "Attorney", "Paralegal / Docketing Clerk", "Read-only / Client"
    };

    public ObservableCollection<string> OcrProviders { get; } = new()
    {
        "None (local / Tesseract)",
        "Azure AI Vision",
        "Google Cloud Vision",
        "AWS Textract"
    };

    public ObservableCollection<UserAction> RecentAuditEntries { get; } = new();
    public ObservableCollection<string> RecentBackups { get; } = new();

    public ICommand VerifyChainCommand { get; }
    public ICommand SaveApiKeysCommand { get; }
    public ICommand ClearApiKeysCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand RefreshBackupsCommand { get; }

    public SettingsViewModel()
    {
        VerifyChainCommand = new RelayCommand(VerifyChain);
        SaveApiKeysCommand = new RelayCommand(SaveApiKeys);
        ClearApiKeysCommand = new RelayCommand(ClearApiKeys);
        BackupNowCommand = new RelayCommand(BackupNow);
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        RefreshBackupsCommand = new RelayCommand(RefreshBackupsList);

        BackupFolder = App.Backups.BackupDirectory;
        BackupStatus = App.Backups.LastStatus;
        App.Backups.BackupCompleted += () =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                BackupStatus = App.Backups.LastStatus;
                RefreshBackupsList();
            });
        };

        Load();
        LoadApiKeys();
        RefreshBackupsList();
    }

    private void Load()
    {
        RecentAuditEntries.Clear();
        foreach (var entry in App.Audit.GetRecent(30))
            RecentAuditEntries.Add(entry);
    }

    private void VerifyChain()
    {
        var ok = App.Audit.VerifyChainIntegrity();
        ChainStatus = ok
            ? $"Verified OK at {DateTime.Now:g} - every record's hash matches its predecessor, chain intact."
            : "INTEGRITY FAILURE - a record's hash does not match. The ledger may have been altered.";
    }

    private void LoadApiKeys()
    {
        try
        {
            // Prefer encrypted file; migrate old plaintext if present
            var plainLegacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IPDocketing", "api-keys.json");

            string? json = null;
            if (File.Exists(KeysEncPath))
                json = EncryptionService.DecryptStringFromFile(KeysEncPath);
            else if (File.Exists(plainLegacy))
            {
                json = File.ReadAllText(plainLegacy);
                // Migrate to encrypted
                EncryptionService.EncryptStringToFile(json, KeysEncPath);
                try { File.Delete(plainLegacy); } catch { /* ignore */ }
            }

            if (string.IsNullOrWhiteSpace(json)) return;
            var data = JsonSerializer.Deserialize<ApiKeysConfig>(json);
            if (data is null) return;

            UsptoApiKey = data.UsptoApiKey ?? "";
            UsptoClientId = data.UsptoClientId ?? "";
            EpoConsumerKey = data.EpoConsumerKey ?? "";
            EpoConsumerSecret = data.EpoConsumerSecret ?? "";
            WipoApiKey = data.WipoApiKey ?? "";
            OcrApiKey = data.OcrApiKey ?? "";
            if (!string.IsNullOrWhiteSpace(data.OcrProvider))
                OcrProvider = data.OcrProvider;
        }
        catch
        {
            ApiKeysStatus = "Could not load API keys (wrong Windows user or corrupted file).";
        }
    }

    private void SaveApiKeys()
    {
        try
        {
            var data = new ApiKeysConfig
            {
                UsptoApiKey = UsptoApiKey,
                UsptoClientId = UsptoClientId,
                EpoConsumerKey = EpoConsumerKey,
                EpoConsumerSecret = EpoConsumerSecret,
                WipoApiKey = WipoApiKey,
                OcrApiKey = OcrApiKey,
                OcrProvider = OcrProvider
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            EncryptionService.EncryptStringToFile(json, KeysEncPath);

            // Remove any leftover plaintext
            var plainLegacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IPDocketing", "api-keys.json");
            if (File.Exists(plainLegacy))
                try { File.Delete(plainLegacy); } catch { /* ignore */ }

            ApiKeysStatus = $"Encrypted & saved at {DateTime.Now:g} (Windows-user locked)";
            App.Audit.Log("Settings", "ApiKeys", 0, "API keys updated (encrypted; values not logged)");
        }
        catch (Exception ex)
        {
            ApiKeysStatus = $"Save failed: {ex.Message}";
        }
    }

    private void ClearApiKeys()
    {
        UsptoApiKey = "";
        UsptoClientId = "";
        EpoConsumerKey = "";
        EpoConsumerSecret = "";
        WipoApiKey = "";
        OcrApiKey = "";
        OcrProvider = "None (local / Tesseract)";
        try
        {
            if (File.Exists(KeysEncPath))
                File.Delete(KeysEncPath);
        }
        catch { /* ignore */ }
        ApiKeysStatus = "API keys cleared.";
    }

    private void BackupNow()
    {
        App.Backups.BackupNow("manual");
        BackupStatus = App.Backups.LastStatus;
        RefreshBackupsList();
        App.Audit.Log("Settings", "Backup", 0, "Manual encrypted backup requested");
    }

    private void OpenBackupFolder()
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
            BackupStatus = $"Could not open folder: {ex.Message}";
        }
    }

    private void RefreshBackupsList()
    {
        RecentBackups.Clear();
        foreach (var path in App.Backups.ListBackups().Take(20))
            RecentBackups.Add(Path.GetFileName(path));
        BackupStatus = App.Backups.LastStatus;
        BackupFolder = App.Backups.BackupDirectory;
    }

    partial void OnCurrentUserChanged(string value) => App.Audit.CurrentUser = value;

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
