using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Services;

/// <summary>
/// Applies an accent palette across the app.
///
/// WHY THE PREVIOUS APPROACH DIDN'T WORK
///
/// "Colorful" used to merge Themes/ColorfulAccent.xaml into
/// Application.Resources.MergedDictionaries at startup, and required a restart.
/// Two things were wrong with that:
///
///  1. It only reached the 13 keys that file happens to redefine. The accent
///     also lives in AcrylicBrush.TintColor on AccentGlassButtonBrush inside
///     LiquidGlass.xaml's ThemeDictionaries, and in the gradient stops of
///     LiquidPrimaryBrush - none of which ColorfulAccent.xaml touches. So even
///     when the merge worked, every accent button, the nav selection and the
///     primary gradient stayed blue. The result looked like the theme "didn't
///     work" because most of what you actually see is accented by those brushes,
///     not by AccentBrush.
///
///  2. Merging a dictionary after startup does not re-resolve StaticResource
///     lookups that have already happened, and brushes inside ThemeDictionaries
///     are re-created on theme change from the original dictionary - so the
///     override could be silently reverted by switching Light/Dark.
///
/// WHAT THIS DOES INSTEAD
///
/// Every element that uses {StaticResource AccentBrush} holds a reference to
/// the same SolidColorBrush OBJECT. Setting that object's Color property
/// repaints all of them immediately - no dictionary merging, no restart, no
/// re-resolution needed. The same applies to an AcrylicBrush's TintColor and to
/// a gradient's stops.
///
/// So this walks the known accent-carrying resources and re-colours them in
/// place. It is applied at startup AND on every theme change, because WinUI
/// rebuilds ThemeDictionary brushes when the element theme flips.
/// </summary>
public static class AccentPaletteService
{
    public sealed record Palette(
        string Name,
        Color Accent,
        Color AccentDeep,
        Color AccentLight,
        Color Danger,
        Color Warning,
        Color Info,
        Color Success);

    /// <summary>The stock blue palette - the values originally in LiquidGlass.xaml.</summary>
    public static readonly Palette Default = new(
        "Default",
        Accent: FromHex("#5B8CFF"),
        AccentDeep: FromHex("#3F6FE8"),
        AccentLight: FromHex("#73A2FF"),
        Danger: FromHex("#FF5B52"),
        Warning: FromHex("#FFAA24"),
        Info: FromHex("#8A7DFF"),
        Success: FromHex("#35D071"));

    /// <summary>Violet-led, higher chroma. Matches Themes/ColorfulAccent.xaml.</summary>
    public static readonly Palette Colorful = new(
        "Colorful",
        Accent: FromHex("#7A3DF5"),
        AccentDeep: FromHex("#5B23D6"),
        AccentLight: FromHex("#9B6BFF"),
        Danger: FromHex("#FF5C8A"),
        Warning: FromHex("#FFA23D"),
        Info: FromHex("#3DD0F5"),
        Success: FromHex("#2FE0A0"));

    public static Palette ForName(string? name) =>
        string.Equals(name, "Colorful", StringComparison.OrdinalIgnoreCase) ? Colorful : Default;

    /// <summary>
    /// Re-colours every accent-carrying resource in place. Safe to call
    /// repeatedly and safe to call before any window exists.
    /// </summary>
    public static void Apply(Palette palette)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        // Plain status brushes.
        SetSolid(resources, "AccentBrush", palette.Accent);
        SetSolid(resources, "DangerBrush", palette.Danger);
        SetSolid(resources, "WarningBrush", palette.Warning);
        SetSolid(resources, "InfoBrush", palette.Info);
        SetSolid(resources, "SuccessBrush", palette.Success);

        // The raw Color resources, so anything resolving those later agrees.
        SetColor(resources, "Accent", palette.Accent);
        SetColor(resources, "Danger", palette.Danger);
        SetColor(resources, "Warning", palette.Warning);
        SetColor(resources, "Info", palette.Info);
        SetColor(resources, "Success", palette.Success);

        // The accent button's acrylic tint - this is the one the old approach
        // missed entirely, and it is what paints every primary button.
        SetAcrylicTint(resources, "AccentGlassButtonBrush", palette.AccentDeep);
        SetAcrylicTint(resources, "NavigationViewItemBackgroundSelected", palette.AccentDeep);
        SetAcrylicTint(resources, "NavigationViewItemBackgroundSelectedPointerOver", palette.Accent);

        // The primary gradient, top-to-bottom light -> deep.
        SetGradient(resources, "LiquidPrimaryBrush",
            palette.AccentLight, palette.AccentDeep, palette.AccentDeep);
    }

    /// <summary>Reads the saved theme name and applies the matching palette.</summary>
    public static void ApplySaved(string appDataDirectory)
    {
        try
        {
            var path = System.IO.Path.Combine(appDataDirectory, "theme-preference.txt");
            var saved = System.IO.File.Exists(path)
                ? System.IO.File.ReadAllText(path).Trim()
                : "Dark";
            Apply(ForName(saved));
        }
        catch
        {
            Apply(Default);
        }
    }

    // --- resource mutators -------------------------------------------------
    //
    // Each is defensive: a theme dictionary may hold a different brush type
    // than expected under High Contrast, where the whole palette is
    // deliberately replaced with system colours and must NOT be overridden.

    private static void SetSolid(ResourceDictionary resources, string key, Color color)
    {
        if (!resources.TryGetValue(key, out var value)) return;
        if (value is SolidColorBrush brush) brush.Color = color;
    }

    private static void SetColor(ResourceDictionary resources, string key, Color color)
    {
        if (!resources.ContainsKey(key)) return;
        if (resources[key] is Color) resources[key] = color;
    }

    private static void SetAcrylicTint(ResourceDictionary resources, string key, Color color)
    {
        if (!resources.TryGetValue(key, out var value)) return;

        switch (value)
        {
            case AcrylicBrush acrylic:
                acrylic.TintColor = color;
                // Fallback is what shows when transparency is off - keeping it
                // in step matters, because a machine with transparency disabled
                // would otherwise never see the palette at all.
                acrylic.FallbackColor = color;
                break;

            // High Contrast substitutes SolidColorBrush here; leave it alone.
            case SolidColorBrush when IsHighContrast():
                break;

            case SolidColorBrush solid:
                solid.Color = color;
                break;
        }
    }

    private static void SetGradient(ResourceDictionary resources, string key, params Color[] stops)
    {
        if (!resources.TryGetValue(key, out var value)) return;
        if (value is not LinearGradientBrush gradient) return;

        for (var i = 0; i < gradient.GradientStops.Count && i < stops.Length; i++)
            gradient.GradientStops[i].Color = stops[i];
    }

    private static bool IsHighContrast()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { return false; }
    }

    private static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return Color.FromArgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16),
            Convert.ToByte(hex.Substring(6, 2), 16));
    }
}
