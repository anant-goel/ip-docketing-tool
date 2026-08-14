using System.Windows;
using System.Windows.Forms;
using IPDocketing.App.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace IPDocketing.App;

public partial class MainWindow : Window
{
    private NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetupTrayIcon();
        RaiseOverdueToastIfAny();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "IP Docketing"
        };

        _trayIcon.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
    }

    /// <summary>
    /// Loads the app logo (Assets/app.ico, embedded as a WPF resource so it
    /// survives single-file publish) for the tray icon. Falls back to the
    /// generic system icon if the resource can't be read for any reason,
    /// so a missing/corrupt icon file never crashes startup.
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is not null)
                return new System.Drawing.Icon(info.Stream);
        }
        catch
        {
            // Fall through to the system default below.
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>
    /// Native Windows notification for overdue hard deadlines, shown via the
    /// tray icon balloon tip (works out-of-the-box without app packaging;
    /// swap for Microsoft.Toolkit.Uwp.Notifications toast XML if the app is
    /// later packaged with an AppUserModelID / MSIX identity).
    /// </summary>
    private void RaiseOverdueToastIfAny()
    {
        var overdue = App.Deadlines.GetOverdue();
        if (overdue.Count == 0 || _trayIcon is null) return;

        _trayIcon.BalloonTipTitle = "IP Docketing - Overdue deadlines";
        _trayIcon.BalloonTipText = $"{overdue.Count} deadline(s) are overdue. Open Deadlines to review.";
        _trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _trayIcon.ShowBalloonTip(6000);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _trayIcon?.Dispose();
    }
}
