using System.Globalization;
using System.Windows.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.App.Converters;

public class UrgencyToLabelConverter : IValueConverter
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
            UrgencyLevel.Overdue => "Overdue",
            UrgencyLevel.Upcoming => "Upcoming",
            UrgencyLevel.PendingResponse => "Pending",
            UrgencyLevel.Completed => "Completed",
            _ => "-"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
