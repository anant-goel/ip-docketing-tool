using System.Windows;
using System.Windows.Forms;
using IPDocketing.App.Services;
using Application = System.Windows.Application;

namespace IPDocketing.App;

public partial class MainWindow : Window
{
    private NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        // Prefer Acrylic (liquid glass blur); falls back to Mica on failure
        SourceInitialized += (_, _) => SystemBackdrop.TryApply(this, BackdropKind.Acrylic);
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

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is not null)
                return new System.Drawing.Icon(info.Stream);
        }
        catch { /* fall through */ }

        return System.Drawing.SystemIcons.Application;
    }

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
