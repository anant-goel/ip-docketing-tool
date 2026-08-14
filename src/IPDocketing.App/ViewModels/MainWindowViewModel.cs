using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IPDocketing.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem { Key = "Dashboard", Label = "Dashboard", Glyph = "\uE80F" },
        new NavItem { Key = "Matters",   Label = "Matters / Portfolio", Glyph = "\uE7C3" },
        new NavItem { Key = "Deadlines", Label = "Deadlines", Glyph = "\uE787" },
        new NavItem { Key = "PtoSync",   Label = "PTO Sync", Glyph = "\uE895" },
        new NavItem { Key = "Documents", Label = "Documents", Glyph = "\uE8A5" },
        new NavItem { Key = "Reports",   Label = "Reports", Glyph = "\uE9D9" },
        new NavItem { Key = "Settings",  Label = "Settings", Glyph = "\uE713" },
    };

    [ObservableProperty]
    private NavItem selectedNavItem;

    [ObservableProperty]
    private object currentView = null!;

    [ObservableProperty]
    private string syncStatusText = "Local database ready";

    [ObservableProperty]
    private string ptoConnectivityText = "PTO: not connected";

    [ObservableProperty]
    private string ocrStatusText = "OCR: idle";

    public ICommand NavigateCommand { get; }

    public MainWindowViewModel()
    {
        selectedNavItem = NavItems[0];
        NavigateCommand = new RelayCommand<NavItem>(Navigate);
        Navigate(NavItems[0]);
    }

    private void Navigate(NavItem? item)
    {
        if (item is null) return;
        SelectedNavItem = item;

        CurrentView = item.Key switch
        {
            "Dashboard" => new DashboardViewModel(),
            "Matters" => new MattersViewModel(),
            "Deadlines" => new DeadlinesViewModel(),
            "PtoSync" => new PtoSyncViewModel(),
            "Documents" => new DocumentsViewModel(),
            "Reports" => new ReportsViewModel(),
            "Settings" => new SettingsViewModel(),
            _ => new DashboardViewModel()
        };
    }
}
