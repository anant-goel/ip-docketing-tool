using System.Diagnostics;
using System.Linq;
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
        try { RefreshGmailUi(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Gmail UI init failed: {ex}"); }
        try
        {
            InitThemePicker();
            LoadApiKeys();
            RefreshBackupUi();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SettingsPage init failed: {ex}");
        }
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

    private static string GmailCredentialPath =>
        System.IO.Path.Combine(App.AppDataDirectory, "gmail_client_secret.json");

    private void RefreshGmailUi()
    {
        var exists = System.IO.File.Exists(GmailCredentialPath);

        GmailStatusText.Text = exists ? "Configured" : "Not set up";
        GmailStatusText.Foreground = exists
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WarningBrush"];

        GmailPathText.Text = exists
            ? $"Using: {GmailCredentialPath}"
            : $"Expected at: {GmailCredentialPath}";
    }

    /// <summary>
    /// Copies a Google OAuth client JSON into the app's data folder.
    ///
    /// The file is validated before it is accepted. A downloaded Google
    /// credentials file can be one of several things - an OAuth client, an API
    /// key, or a service account - and only an OAuth client of type "installed"
    /// works for a desktop app reading your own mailbox. Copying the wrong one
    /// into place would leave the app looking configured and then failing at
    /// authorisation with a message from deep inside the Google library that
    /// explains nothing.
    /// </summary>
    private async void ChooseGmailCredentials_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var text = await Windows.Storage.FileIO.ReadTextAsync(file);

            using var parsed = System.Text.Json.JsonDocument.Parse(text);
            var root = parsed.RootElement;

            var hasInstalled = root.TryGetProperty("installed", out var installed);
            var hasWeb = root.TryGetProperty("web", out _);

            if (root.TryGetProperty("type", out var type) &&
                type.GetString() == "service_account")
            {
                await Info("Wrong credential type",
                    "That's a service account key. Service accounts can't read a personal Gmail mailbox — " +
                    "they have no inbox of their own, and reaching yours would need domain-wide delegation.\n\n" +
                    "You need an OAuth client ID of type 'Desktop app'.");
                return;
            }

            if (!hasInstalled && !hasWeb)
            {
                await Info("Not an OAuth client file",
                    "This JSON has no 'installed' or 'web' section, so it isn't an OAuth client ID.\n\n" +
                    "In Google Cloud Console: APIs & Services > Credentials > Create credentials > " +
                    "OAuth client ID > Desktop app, then Download JSON.");
                return;
            }

            if (hasWeb && !hasInstalled)
            {
                await Info("Wrong OAuth client type",
                    "That's a 'Web application' client. A desktop app needs the 'Desktop app' type — " +
                    "the web type expects a redirect URI this app can't provide.");
                return;
            }

            if (!installed.TryGetProperty("client_id", out _))
            {
                await Info("Incomplete credentials", "The 'installed' section has no client_id.");
                return;
            }

            System.IO.Directory.CreateDirectory(App.AppDataDirectory);
            System.IO.File.WriteAllText(GmailCredentialPath, text);

            App.Audit.Log("Configure", "Gmail", 0,
                "Gmail OAuth client credentials installed. File contents are not logged.");

            RefreshGmailUi();
            await Info("Gmail configured",
                "Saved. The first time you use OTP auto-fill, a browser window will open asking you to " +
                "grant read-only Gmail access. That consent is between you and Google.");
        }
        catch (System.Text.Json.JsonException)
        {
            await Info("Not valid JSON", "That file couldn't be parsed as JSON at all.");
        }
        catch (Exception ex)
        {
            await Info("Couldn't save", ex.Message);
        }
    }

    private async void RemoveGmailCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (!System.IO.File.Exists(GmailCredentialPath))
        {
            await Info("Nothing to remove", "No Gmail credentials are stored.");
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove Gmail credentials?",
            Content = "This deletes the client JSON and the stored authorisation token. " +
                      "OTP auto-fill will stop working until you set it up again.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            System.IO.File.Delete(GmailCredentialPath);

            // The token store holds a live refresh token - leaving it behind
            // would mean the app still had standing access to the mailbox after
            // you thought you'd revoked it.
            var tokenStore = System.IO.Path.Combine(App.AppDataDirectory, "gmail_token_store");
            if (System.IO.Directory.Exists(tokenStore))
                System.IO.Directory.Delete(tokenStore, recursive: true);

            App.Audit.Log("Configure", "Gmail", 0, "Gmail credentials and token store removed.");
            RefreshGmailUi();

            await Info("Removed",
                "Deleted locally. To fully revoke access, also remove this app at " +
                "myaccount.google.com > Security > Third-party access.");
        }
        catch (Exception ex)
        {
            await Info("Couldn't remove", ex.Message);
        }
    }

    private async void OpenGmailFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(App.AppDataDirectory);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(App.AppDataDirectory);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            await Info("Couldn't open folder", ex.Message);
        }
    }

    private async void GmailHelp_Click(object sender, RoutedEventArgs e) =>
        await Info("Setting up Gmail OTP auto-fill",
            "1. console.cloud.google.com — create a project (or pick an existing one).\n\n" +
            "2. APIs & Services > Library > enable the Gmail API.\n\n" +
            "3. APIs & Services > OAuth consent screen > External. Add your own Gmail address " +
            "under Test users — otherwise Google blocks the sign-in.\n\n" +
            "4. APIs & Services > Credentials > Create credentials > OAuth client ID > " +
            "Application type: Desktop app. Download the JSON.\n\n" +
            "5. Back here: 'Choose credentials file...' and pick that JSON.\n\n" +
            "The scope is gmail.readonly — the app can read messages to find the OTP and nothing else. " +
            "It cannot send, delete or modify mail. The credentials and the token stay on this machine; " +
            "nothing is transmitted to me or to Anthropic.");

    private async System.Threading.Tasks.Task Info(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                MaxHeight = 380
            },
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Restores the selected encrypted backup over the live database.
    ///
    /// This is destructive and irreversible in the obvious way, so it takes a
    /// safety snapshot of the CURRENT database first - restoring the wrong file
    /// should not be the end of your data - and then requires a restart,
    /// because EF Core is holding an open connection to the file being replaced.
    /// Swapping it underneath a live DbContext produces corruption rather than a
    /// clean restore.
    /// </summary>
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not string selected || string.IsNullOrWhiteSpace(selected))
        {
            await Info("Nothing selected", "Pick a backup from the list first.");
            return;
        }

        var backupDir = System.IO.Path.Combine(App.AppDataDirectory, "Backups");
        var path = System.IO.Path.Combine(backupDir, selected.Split(' ')[0]);

        if (!System.IO.File.Exists(path))
        {
            // The list shows a formatted label; fall back to matching by prefix.
            var match = System.IO.Directory.GetFiles(backupDir, "*.enc")
                .FirstOrDefault(f => selected.Contains(System.IO.Path.GetFileName(f), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                await Info("Backup not found", $"Couldn't locate the file for:\n{selected}");
                return;
            }
            path = match;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Restore this backup?",
            Content = new TextBlock
            {
                Text = $"This replaces your current database with:\n\n{System.IO.Path.GetFileName(path)}\n\n" +
                       "A snapshot of the current database is taken first, so this is undoable.\n\n" +
                       "The app must close afterwards — the database file can't be swapped while it's open.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Restore and close",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            App.Backups.SnapshotBeforeDestructiveChange("pre-restore");

            // Written beside the live DB and swapped in on next launch, because
            // the current process still has it open.
            var pending = App.DatabasePath + ".restore-pending";
            IPDocketing.Core.Services.EncryptionService.DecryptFileTo(path, pending);

            App.Audit.Log("Restore", "Database", 0,
                $"Restore staged from {System.IO.Path.GetFileName(path)}; applies on next launch.");

            await Info("Restore staged",
                "The app will now close. On next launch the restored database is put in place.\n\n" +
                "If anything looks wrong afterwards, the pre-restore snapshot is in the Backups folder.");

            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            await Info("Restore failed", ex.Message);
        }
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

    private bool _themeInitializing;

    private void InitThemePicker()
    {
        _themeInitializing = true;
        var saved = (App.MainWindow as MainWindow)?.GetSavedThemeSetting() ?? "Dark";
        foreach (var item in ThemeBox.Items.OfType<ComboBoxItem>())
        {
            if ((string)item.Tag == saved)
            {
                ThemeBox.SelectedItem = item;
                break;
            }
        }
        ThemeRestartNote.Visibility = saved == "Colorful"
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        _themeInitializing = false;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_themeInitializing) return;
        if (ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        var theme = tag switch
        {
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            "System" => Microsoft.UI.Xaml.ElementTheme.Default,
            _ => Microsoft.UI.Xaml.ElementTheme.Dark
        };
        (App.MainWindow as MainWindow)?.SetTheme(theme, tag);

        ThemeRestartNote.Visibility = tag == "Colorful"
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
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
