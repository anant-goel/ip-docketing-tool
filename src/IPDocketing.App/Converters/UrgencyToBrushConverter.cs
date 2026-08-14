using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using IPDocketing.Core.Models;

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
