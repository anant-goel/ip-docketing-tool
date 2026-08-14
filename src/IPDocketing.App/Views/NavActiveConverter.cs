using System.Globalization;
using System.Windows.Data;
using IPDocketing.App.ViewModels;

namespace IPDocketing.App.Views;

/// <summary>
/// Returns "Active" when the nav item bound to a rail button matches the
/// currently selected nav item, so the NavRailButtonStyle trigger can
/// highlight it (Fluent-style active-state accent fill).
/// </summary>
public class NavActiveConverter : IMultiValueConverter
{
    public static readonly NavActiveConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return "Inactive";
        if (values[0] is NavItem thisItem && values[1] is NavItem selected && thisItem.Key == selected.Key)
            return "Active";
        return "Inactive";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
