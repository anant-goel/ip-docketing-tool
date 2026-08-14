using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPDocketing.Core.Models;

namespace IPDocketing.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool isDarkMode;

    [ObservableProperty]
    private string currentUser = "local.user";

    [ObservableProperty]
    private string currentRole = "Attorney";

    [ObservableProperty]
    private string chainStatus = "Not yet verified";

    public ObservableCollection<string> Roles { get; } = new()
    {
        "Administrator", "Attorney", "Paralegal / Docketing Clerk", "Read-only / Client"
    };

    public ObservableCollection<UserAction> RecentAuditEntries { get; } = new();

    public ICommand VerifyChainCommand { get; }

    public SettingsViewModel()
    {
        VerifyChainCommand = new RelayCommand(VerifyChain);
        Load();
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

    partial void OnCurrentUserChanged(string value) => App.Audit.CurrentUser = value;
}
