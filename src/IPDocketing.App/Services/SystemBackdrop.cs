using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace IPDocketing.App.Services;

public enum BackdropKind
{
    Acrylic,  // DWM transient → desktop blur
    Mica,     // DWM main window
    MicaAlt   // DWM tabbed
}

/// <summary>
/// WPF system materials via DwmSetWindowAttribute (Windows 11+).
///
/// Fallback order when requesting Acrylic:
///   1. DWMSBT_TRANSIENTWINDOW (Acrylic)
///   2. DWMSBT_TABBEDWINDOW (Mica Alt)
///   3. DWMSBT_MAINWINDOW (Mica)
///   4. Legacy DWMWA_MICA_EFFECT
///   5. Leave window unchanged (caller keeps theme brushes)
/// </summary>
public static class SystemBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_MICA_EFFECT = 1029;

    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMSBT_TABBEDWINDOW = 4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);

    public static bool TryApply(Window window, BackdropKind kind = BackdropKind.Acrylic)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return false;

            DwmIsCompositionEnabled(out var composition);
            if (!composition) return false;

            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            // Build attempt list from requested kind
            var attempts = kind switch
            {
                BackdropKind.Acrylic => new[] { DWMSBT_TRANSIENTWINDOW, DWMSBT_TABBEDWINDOW, DWMSBT_MAINWINDOW },
                BackdropKind.MicaAlt => new[] { DWMSBT_TABBEDWINDOW, DWMSBT_MAINWINDOW },
                _ => new[] { DWMSBT_MAINWINDOW }
            };

            foreach (var backdrop in attempts)
            {
                int value = backdrop;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) == 0)
                {
                    window.Background = Brushes.Transparent;
                    return true;
                }
            }

            int mica = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref mica, sizeof(int)) == 0)
            {
                window.Background = Brushes.Transparent;
                return true;
            }
        }
        catch
        {
            // OS / policy blocked
        }

        return false;
    }
}
