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
        ApplySavedTheme();
        StartAmbientDrift();

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

                // Hover and press states default to an opaque grey square, which
                // on a glass title bar reads as a hole punched in the material.
                // A translucent white wash keeps the caption buttons on the same
                // surface as everything else.
                _appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(38, 255, 255, 255);
                _appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(64, 255, 255, 255);
                _appWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;
                _appWindow.TitleBar.ButtonPressedForegroundColor = Colors.White;
                _appWindow.TitleBar.ButtonInactiveForegroundColor = Color.FromArgb(150, 255, 255, 255);
            }
        }
        catch
        {
            // Non-critical chrome setup
        }
    }

    /// <summary>
    /// Starts the slow drift of the colour field behind the glass.
    ///
    /// The storyboard lives in RootGrid.Resources under a key rather than a
    /// name, because a Storyboard in a resource dictionary is addressed by key -
    /// and looking it up here is what lets the animation stay entirely declarative
    /// in the XAML while still being startable from code.
    ///
    /// Failure is swallowed on purpose. If the animation cannot start, the orbs
    /// simply sit still and the window still looks right; taking the app down
    /// over a decorative effect would be absurd.
    /// </summary>
    private void StartAmbientDrift()
    {
        try
        {
            if (RootGrid.Resources.TryGetValue("AmbientDrift", out var resource) &&
                resource is Storyboard drift)
            {
                drift.Begin();
            }
        }
        catch
        {
            // Decoration only - a still background is a fine outcome.
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

    /// <summary>
    /// Reads the saved theme preference (Dark / Light / System) and applies
    /// it to the whole window. Defaults to Dark, since Liquid Glass was
    /// designed dark-first — the Light and HighContrast dictionaries exist
    /// in LiquidGlass.xaml too (see ThemeDictionaries there), this just
    /// controls which one is active and remembers the choice.
    ///
    /// Uses a plain file in AppDataDirectory rather than
    /// Windows.Storage.ApplicationData.Current.LocalSettings -
    /// ApplicationData.Current requires MSIX package identity and throws
    /// immediately in an unpackaged app (which this is, deliberately -
    /// WindowsPackageType=None). That was the actual cause of the crash on
    /// every launch: an unhandled exception right here, in the MainWindow
    /// constructor, before any window could show.
    /// </summary>
    private static string ThemeSettingPath => Path.Combine(App.AppDataDirectory, "theme-preference.txt");

    private void ApplySavedTheme()
    {
        var saved = ReadSavedTheme();
        RootGrid.RequestedTheme = saved switch
        {
            "Light" => ElementTheme.Light,
            "System" => ElementTheme.Default,
            // "Colorful" is an accent palette layered on the dark base.
            _ => ElementTheme.Dark
        };

        try
        {
            Services.AccentPaletteService.Apply(Services.AccentPaletteService.ForName(saved));
        }
        catch
        {
            // Cosmetic only.
        }
    }

    /// <summary>Called from SettingsPage when the user changes the theme picker.</summary>
    public void SetTheme(ElementTheme theme, string settingValue)
    {
        RootGrid.RequestedTheme = theme;

        // Colorful is an accent palette, not a light/dark mode - it sits on top
        // of Dark. Selecting it keeps the dark base and swaps the accents.
        if (settingValue == "Colorful") RootGrid.RequestedTheme = ElementTheme.Dark;

        // WinUI rebuilds ThemeDictionary brushes when the element theme flips,
        // which throws away in-place colour changes. Re-applying afterwards is
        // what makes the palette survive a Light/Dark switch.
        try
        {
            Services.AccentPaletteService.Apply(
                Services.AccentPaletteService.ForName(settingValue));
        }
        catch
        {
            // Cosmetic only - never worth failing a theme change over.
        }

        try
        {
            Directory.CreateDirectory(App.AppDataDirectory);
            File.WriteAllText(ThemeSettingPath, settingValue);
        }
        catch
        {
            // Theme still applies for this session even if the write fails;
            // it just won't be remembered next launch.
        }
    }

    public string GetSavedThemeSetting() => ReadSavedTheme();

    private static string ReadSavedTheme()
    {
        try
        {
            return File.Exists(ThemeSettingPath) ? File.ReadAllText(ThemeSettingPath).Trim() : "Dark";
        }
        catch
        {
            return "Dark";
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
            "Oppositions" => typeof(OppositionsPage),
            "Automation" => typeof(AutomationPage),
            "Renewals" => typeof(RenewalsPage),
            "StatusTracker" => typeof(StatusTrackerPage),
            "TrademarkSearch" => typeof(TrademarkSearchPage),
            "Journal" => typeof(JournalPage),
            "ClientUpdates" => typeof(ClientUpdatesPage),
            "ActivityLog" => typeof(ActivityLogPage),
            "IpIndiaPortal" => typeof(IpIndiaPortalPage),
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

        if (query.Contains("status", StringComparison.OrdinalIgnoreCase)
            || query.Contains("history", StringComparison.OrdinalIgnoreCase))
        {
            SelectNavigationItem("StatusTracker");
            return;
        }

        if (query.Contains("opposition", StringComparison.OrdinalIgnoreCase)
            || query.Contains("oppose", StringComparison.OrdinalIgnoreCase))
        {
            SelectNavigationItem("Oppositions");
            return;
        }

        if (query.Contains("renew", StringComparison.OrdinalIgnoreCase)
            || query.Contains("expir", StringComparison.OrdinalIgnoreCase)
            || query.Contains("restor", StringComparison.OrdinalIgnoreCase))
        {
            SelectNavigationItem("Renewals");
            return;
        }

        if (query.Contains("journal", StringComparison.OrdinalIgnoreCase)
            || query.Contains("watch", StringComparison.OrdinalIgnoreCase))
        {
            SelectNavigationItem("Journal");
            return;
        }

        var deadlineSearch = query.Contains("deadline", StringComparison.OrdinalIgnoreCase)
                             || query.Contains("due", StringComparison.OrdinalIgnoreCase)
                             || query.Contains("response", StringComparison.OrdinalIgnoreCase);

        // Anything else is treated as a mark name and handed to the dedicated
        // trademark search, which is what "search matters" nearly always means.
        SelectNavigationItem(deadlineSearch ? "Deadlines" : "TrademarkSearch");
    }
}
