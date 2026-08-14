using IPDocketing.WinUI.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace IPDocketing.WinUI;

public sealed partial class MainWindow : Window
{
    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        Title = "IP Docketing";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        ApplySystemBackdrop();

        DateText.Text = DateTime.Now.ToString("ddd, dd MMM yyyy");
    }

    private void ConfigureWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Resize(new SizeInt32(1420, 900));
            _appWindow.SetIcon("Assets\\app.ico");

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
        catch
        {
            // Window sizing and icon decoration are non-critical.
        }
    }

    /// <summary>
    /// Mica is the durable window foundation recommended for long-lived WinUI apps.
    /// Windows 10 falls back to Desktop Acrylic; unsupported/policy-disabled devices
    /// still receive an explicit readable solid background.
    /// </summary>
    private void ApplySystemBackdrop()
    {
        RootGrid.Background = new SolidColorBrush(Color.FromArgb(255, 8, 11, 18));

        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
                return;
            }

            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
            }
        }
        catch
        {
            // The solid brush remains visible when composition is unavailable.
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is null)
            NavigateTo("Dashboard");
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo("Settings");
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "Dashboard" => typeof(DashboardPage),
            "Matters" => typeof(MattersPage),
            "Deadlines" => typeof(DeadlinesPage),
            "PtoSync" => typeof(PtoSyncPage),
            "Documents" => typeof(DocumentsPage),
            "Reports" => typeof(ReportsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string itemTag && itemTag == tag)
            {
                NavView.SelectedItem = item;
                NavigateTo(tag);
                return;
            }
        }
    }

    private void QuickDocket_Click(object sender, RoutedEventArgs e) =>
        SelectNavigationItem("Deadlines");

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return;

        var deadlineSearch = query.Contains("deadline", StringComparison.OrdinalIgnoreCase)
                             || query.Contains("due", StringComparison.OrdinalIgnoreCase)
                             || query.Contains("response", StringComparison.OrdinalIgnoreCase);
        SelectNavigationItem(deadlineSearch ? "Deadlines" : "Matters");
    }
}
