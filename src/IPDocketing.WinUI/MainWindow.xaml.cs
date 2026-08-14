using System.IO;
using IPDocketing.WinUI.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace IPDocketing.WinUI;

public sealed partial class MainWindow : Window
{
    private AppWindow? _appWindow;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _reminderTimer;
    private bool _notificationsRegistered;

    public MainWindow()
    {
        InitializeComponent();
        Title = "IP Docketing | By Anant Goel";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        ApplySystemBackdrop();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        RegisterNotifications();
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _reminderTimer.Tick += (_, _) => RefreshReminders(showSystemNotification: true);
        _reminderTimer.Start();
        RefreshReminders(showSystemNotification: true);

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _reminderTimer.Stop();
            if (!_notificationsRegistered) return;
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch
            {
                // Notification cleanup must not block app shutdown.
            }
        };
    }

    private void UpdateClock()
    {
        if (DateText is null) return;
        DateText.Text = DateTime.Now.ToString("ddd, dd MMM yyyy  HH:mm:ss");
    }

    private void ConfigureWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Resize(new SizeInt32(1420, 900));
            _appWindow.Title = "IP Docketing | By Anant Goel";

            // Taskbar / title icon — use absolute path next to the EXE
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonForegroundColor = Colors.White;
            }
        }
        catch
        {
            // Non-critical chrome setup
        }
    }

    /// <summary>
    /// Prefer Desktop Acrylic for liquid-glass blur of the desktop (reference look).
    /// Fall back to Mica Alt, then solid.
    /// </summary>
    private void ApplySystemBackdrop()
    {
        RootGrid.Background = new SolidColorBrush(Color.FromArgb(255, 8, 11, 18));

        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
                return;
            }

            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
            }
        }
        catch
        {
            // Solid remains
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
            "Calendar" => typeof(CalendarPage),
            "PtoSync" => typeof(PtoSyncPage),
            "Documents" => typeof(DocumentsPage),
            "Reports" => typeof(ReportsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, new DrillInNavigationTransitionInfo());
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

    private void NotificationButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshReminders(showSystemNotification: false);
        ReminderInfoBar.IsOpen = ReminderBadge.Visibility == Visibility.Visible;
        NavigateToCalendar();
    }

    private void ReminderView_Click(object sender, RoutedEventArgs e) => NavigateToCalendar();

    public void NavigateToCalendar() => SelectNavigationItem("Calendar");

    public void RefreshReminders(bool showSystemNotification)
    {
        var overdue = App.Deadlines.GetOverdue();
        var upcoming = App.Deadlines.GetUpcoming(7);
        var attentionCount = overdue.Count + upcoming.Count;

        ReminderCountText.Text = attentionCount > 99 ? "99+" : attentionCount.ToString();
        ReminderBadge.Visibility = attentionCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReminderInfoBar.Message = attentionCount == 0
            ? "No overdue or next-seven-day deadlines."
            : $"{overdue.Count} overdue · {upcoming.Count} due in the next 7 days.";

        if (showSystemNotification && attentionCount > 0 && ShouldSendReminderToday())
            ShowSystemReminder(overdue.Count, upcoming.Count);
    }

    private void RegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
        }
        catch
        {
            // The in-app reminder badge remains available if system notifications are disabled.
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Activate();
            NavigateToCalendar();
        });
    }

    private bool ShouldSendReminderToday()
    {
        try
        {
            var statePath = Path.Combine(App.AppDataDirectory, "last-deadline-reminder.txt");
            return !File.Exists(statePath)
                   || !string.Equals(File.ReadAllText(statePath).Trim(),
                       DateTime.Today.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    private void ShowSystemReminder(int overdueCount, int upcomingCount)
    {
        if (!_notificationsRegistered) return;

        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("page", "Calendar")
                .SetScenario(AppNotificationScenario.Reminder)
                .AddText("IP Docketing deadline reminder")
                .AddText($"{overdueCount} overdue · {upcomingCount} due in the next 7 days")
                .AddButton(new AppNotificationButton("Open calendar")
                    .AddArgument("page", "Calendar"))
                .BuildNotification();

            notification.Expiration = DateTimeOffset.Now.AddDays(1);
            AppNotificationManager.Default.Show(notification);
            File.WriteAllText(
                Path.Combine(App.AppDataDirectory, "last-deadline-reminder.txt"),
                DateTime.Today.ToString("yyyy-MM-dd"));
        }
        catch
        {
            // Notifications can be disabled by Windows policy; the in-app badge still works.
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return;

        if (query.Contains("calendar", StringComparison.OrdinalIgnoreCase)
            || query.Contains("date", StringComparison.OrdinalIgnoreCase))
        {
            SelectNavigationItem("Calendar");
            return;
        }

        var deadlineSearch = query.Contains("deadline", StringComparison.OrdinalIgnoreCase)
                             || query.Contains("due", StringComparison.OrdinalIgnoreCase)
                             || query.Contains("response", StringComparison.OrdinalIgnoreCase);
        SelectNavigationItem(deadlineSearch ? "Deadlines" : "Matters");
    }
}
