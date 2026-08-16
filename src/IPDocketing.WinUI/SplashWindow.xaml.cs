using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace IPDocketing.WinUI;

public sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            appWindow.Resize(new SizeInt32(360, 260));
            appWindow.IsShownInSwitchers = false;

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // Center on the primary display.
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            appWindow.Move(new PointInt32(
                area.WorkArea.X + (area.WorkArea.Width - 360) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - 260) / 2));
        }
        catch
        {
            // Splash chrome is cosmetic - if any of this fails, a plain
            // default window is a fine fallback, not worth crashing over.
        }
    }

    public void SetStatus(string text) => StatusText.Text = text;
}
