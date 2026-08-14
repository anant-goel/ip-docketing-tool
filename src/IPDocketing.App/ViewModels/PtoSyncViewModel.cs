using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPDocketing.Core.Models;

namespace IPDocketing.App.ViewModels;

/// <summary>
/// PTO sync is architected here (source selection, sync log, per-matter
/// linkage) but does not call live USPTO TSDR/PAIR, EPO OPS, or WIPO APIs -
/// those require registered API credentials per office. Wire a real
/// IPtoSyncClient implementation per office behind SyncSelectedSource().
/// </summary>
public partial class PtoSyncViewModel : ViewModelBase
{
    public ObservableCollection<string> SyncLog { get; } = new();

    [ObservableProperty]
    private PtoSource selectedSource = PtoSource.USPTO;

    [ObservableProperty]
    private bool isSyncing;

    [ObservableProperty]
    private string connectivityStatus = "Not connected";

    public Array Sources => Enum.GetValues(typeof(PtoSource));

    public ICommand SyncCommand { get; }
    public ICommand ConnectCommand { get; }

    public PtoSyncViewModel()
    {
        SyncCommand = new RelayCommand(SyncSelectedSource);
        ConnectCommand = new RelayCommand(ConnectSelectedSource);
        SyncLog.Add($"[{DateTime.Now:HH:mm:ss}] PTO Sync panel ready. Configure credentials in Settings to enable live sync.");
    }

    private void ConnectSelectedSource()
    {
        ConnectivityStatus = $"{SelectedSource}: credentials required (see Settings)";
        SyncLog.Add($"[{DateTime.Now:HH:mm:ss}] Connect requested for {SelectedSource} - no API credentials configured in this build.");
    }

    private void SyncSelectedSource()
    {
        IsSyncing = true;
        SyncLog.Add($"[{DateTime.Now:HH:mm:ss}] Sync requested for {SelectedSource}.");
        SyncLog.Add($"[{DateTime.Now:HH:mm:ss}] No live connection configured - this is a scaffold. " +
                     "Implement IPtoSyncClient for USPTO TSDR/PAIR, EPO OPS, or WIPO to pull real Official Actions.");
        IsSyncing = false;
    }
}
