using System.Globalization;
using System.Windows.Data;
using IPDocketing.Core.Models;

// UseWindowsForms is enabled elsewhere in this project (for the tray icon),
// which pulls System.Drawing into implicit scope alongside WPF's
// System.Windows.Media. Color, Brushes, and SolidColorBrush exist in both,
// so they're aliased explicitly to the WPF/Media versions here.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace IPDocketing.App.Converters;

/// <summary>
/// Maps a Deadline's computed UrgencyLevel to the status colors defined in
/// the design spec: Crimson (overdue), Amber (upcoming/warning),
/// Indigo (pending PTO response), Emerald (completed).
/// </summary>
public class UrgencyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value switch
        {
            UrgencyLevel u => u,
            Deadline d => d.GetUrgency(DateTime.Now),
            _ => UrgencyLevel.PendingResponse
        };

        return level switch
        {
            UrgencyLevel.Overdue => new SolidColorBrush(Color.FromRgb(0xDC, 0x14, 0x3C)),
            UrgencyLevel.Upcoming => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            UrgencyLevel.PendingResponse => new SolidColorBrush(Color.FromRgb(0x6A, 0x5A, 0xCD)),
            UrgencyLevel.Completed => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
            _ => Brushes.Gray
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
