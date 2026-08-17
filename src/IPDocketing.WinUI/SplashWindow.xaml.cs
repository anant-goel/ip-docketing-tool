using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
// CompositionTarget is Microsoft.UI.Xaml.Media in WinUI 3, not Microsoft.UI.Xaml
// as it was in WPF - hence CS0103 without this.
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace IPDocketing.WinUI;

/// <summary>
/// The startup splash.
///
/// PHASE 31 FIX - "blank window on launch".
///
/// The old flow was: construct the splash, call Activate(), yield the
/// dispatcher exactly once, then run the whole database bring-up (open, create
/// schema, seed, generate client updates) synchronously on the UI thread.
///
/// Activate() only *requests* that the window be shown. The content is laid
/// out and painted later, when the message loop next gets a turn - and a single
/// Task.Yield does not reliably get you past first paint. The synchronous work
/// then blocked the UI thread outright, so the message loop never ran, the
/// splash never painted, and what you saw was an empty window frame for as long
/// as startup took. On a cold start with schema creation and seeding, that is
/// several seconds of blank.
///
/// Two changes fix it: <see cref="WaitForFirstPaintAsync"/> below gives the
/// caller something real to await (the content's Loaded event plus a rendering
/// tick), and App.OnLaunched now runs the database work on a background thread
/// so the loop keeps turning and the progress bar actually animates.
/// </summary>
public sealed partial class SplashWindow : Window
{
    private readonly TaskCompletionSource _firstPaint = new();

    // Captured on the UI thread at construction. Startup calls SetStatus from a
    // worker thread, and reaching for Window.DispatcherQueue from off-thread is
    // not something to rely on.
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public SplashWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;

        RootPanel.Loaded += (_, _) =>
        {
            // Loaded fires once layout has happened but before the frame is
            // composed. Waiting one further rendering tick means that when this
            // task completes there are genuinely pixels on screen, which is the
            // whole point.
            CompositionTarget.Rendering += OnFirstRender;
        };

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            appWindow.Resize(new SizeInt32(380, 280));
            appWindow.IsShownInSwitchers = false;

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            appWindow.Move(new PointInt32(
                area.WorkArea.X + (area.WorkArea.Width - 380) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - 280) / 2));
        }
        catch
        {
            // Splash chrome is cosmetic - a plain default window is a fine
            // fallback and never worth crashing the app over.
        }
    }

    private void OnFirstRender(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstRender;
        _firstPaint.TrySetResult();
    }

    /// <summary>
    /// Completes once the splash has actually rendered. Callers await this
    /// before starting any real work, so the window is never left blank.
    /// The timeout is a safety valve: if compositing is unavailable (remote
    /// session, no GPU, policy-disabled), startup proceeds regardless rather
    /// than hanging forever on a frame that will never arrive.
    /// </summary>
    public async Task WaitForFirstPaintAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_firstPaint.Task, Task.Delay(timeout));
        if (completed != _firstPaint.Task) _firstPaint.TrySetResult();
    }

    public void SetStatus(string text)
    {
        // Callable from a background thread during startup, so marshal it.
        if (_dispatcher.HasThreadAccess) StatusText.Text = text;
        else _dispatcher.TryEnqueue(() => StatusText.Text = text);
    }
}
