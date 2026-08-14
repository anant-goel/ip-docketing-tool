namespace IPDocketing.Core.Services;

/// <summary>
/// Rolls a nominal statutory end forward to the next working day. Deliberately
/// isolated from period math (RuleEngineService never contains roll logic) so
/// that updating a holiday table can never touch the arithmetic that computes
/// the nominal date - per-office closure calendars can be swapped in here
/// without any change to CountryRule or RuleEngineService.
///
/// Ships with a simple US-federal-style weekend + fixed-date holiday set as a
/// working default. For production use, inject a real per-office calendar
/// (USPTO/DC federal holidays, an EPO filing-office calendar, or a specific
/// national office's closure table for a PCT entry - see PCT Rule 80.5, which
/// rolls against the *national* office of entry, not WIPO).
/// </summary>
public class HolidayCalendarService
{
    public string CalendarVersion { get; } = "US_FEDERAL_SIMPLE_v2025.1";

    private static readonly HashSet<(int Month, int Day)> FixedHolidays = new()
    {
        (1, 1),   // New Year's Day
        (6, 19),  // Juneteenth
        (7, 4),   // Independence Day
        (11, 11), // Veterans Day
        (12, 25), // Christmas Day
    };

    public bool IsWorkingDay(DateTime date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        if (FixedHolidays.Contains((date.Month, date.Day)))
            return false;

        return true;
    }

    /// <summary>Shifts a nominal end to the next working day.</summary>
    public DateTime RollForward(DateTime nominal)
    {
        var effective = nominal.Date;
        while (!IsWorkingDay(effective))
            effective = effective.AddDays(1);
        return effective;
    }
}
