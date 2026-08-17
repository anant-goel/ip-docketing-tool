namespace IPDocketing.Core.Services;

/// <summary>
/// Rolls a nominal statutory end forward to the next working day. Deliberately
/// isolated from period math (RuleEngineService never contains roll logic) so
/// that updating a holiday table can never touch the arithmetic that computes
/// the nominal date - per-office closure calendars can be swapped in here
/// without any change to CountryRule or RuleEngineService.
///
/// PHASE 30 CHANGE - the previous version hard-coded a US-federal weekend +
/// fixed-date set and applied it to every jurisdiction, including India. That
/// is wrong for the workload this app actually handles: an Indian TM deadline
/// was being rolled against Juneteenth while ignoring 26 January. Calendars are
/// now keyed by jurisdiction and resolved per deadline, and the calendar
/// version tag is recorded on the audit entry so a rolled date stays defensible.
///
/// LIMITATION, stated plainly: only fixed-date national holidays are encoded.
/// India's calendar contains many movable/lunar holidays (Holi, Diwali, Eid,
/// Good Friday, Muharram) plus per-branch closures that differ between Delhi,
/// Mumbai, Kolkata, Chennai and Ahmedabad. Those are NOT here and cannot be
/// derived from a formula. Load them each year via AddOfficeClosures from the
/// CGPDTM holiday notification before relying on a rolled date for anything
/// with money or rights attached.
/// </summary>
public class HolidayCalendarService
{
    public const string IndiaCalendarId = "IN";
    public const string UnitedStatesCalendarId = "US";

    /// <summary>Recorded on every deadline's audit entry so a reviewer can see which table was in force.</summary>
    public string CalendarVersion { get; } = "IN_FIXED+US_FEDERAL_v2026.1";

    /// <summary>Jurisdiction used when a deadline carries no country of its own.</summary>
    public string DefaultCalendarId { get; set; } = IndiaCalendarId;

    // Fixed-date national holidays only. Movable feasts are deliberately
    // absent - see the class remark. Both registries observe a Sat/Sun weekend.
    private static readonly Dictionary<string, HashSet<(int Month, int Day)>> FixedHolidays =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [IndiaCalendarId] = new()
            {
                (1, 26),  // Republic Day
                (5, 1),   // May Day (observed by several IP offices)
                (8, 15),  // Independence Day
                (10, 2),  // Gandhi Jayanti
                (12, 25), // Christmas Day
            },
            [UnitedStatesCalendarId] = new()
            {
                (1, 1),   // New Year's Day
                (6, 19),  // Juneteenth
                (7, 4),   // Independence Day
                (11, 11), // Veterans Day
                (12, 25), // Christmas Day
            },
        };

    // Per-jurisdiction extra closures added for a specific year (Diwali, Holi,
    // a branch-office closure notice).
    private readonly Dictionary<string, HashSet<DateTime>> _officeClosures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers additional non-working dates for a jurisdiction - the seam for
    /// pasting in the CGPDTM annual holiday list. Stored date-only.
    /// </summary>
    public void AddOfficeClosures(string calendarId, IEnumerable<DateTime> dates)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) return;
        if (!_officeClosures.TryGetValue(calendarId, out var set))
        {
            set = new HashSet<DateTime>();
            _officeClosures[calendarId] = set;
        }
        foreach (var date in dates) set.Add(date.Date);
    }

    public IReadOnlyCollection<DateTime> GetOfficeClosures(string calendarId) =>
        _officeClosures.TryGetValue(calendarId, out var set)
            ? set.OrderBy(d => d).ToList()
            : Array.Empty<DateTime>();

    public bool IsWorkingDay(DateTime date) => IsWorkingDay(date, DefaultCalendarId);

    public bool IsWorkingDay(DateTime date, string? calendarId)
    {
        var id = Normalize(calendarId);

        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        if (FixedHolidays.TryGetValue(id, out var fixedDays) && fixedDays.Contains((date.Month, date.Day)))
            return false;

        if (_officeClosures.TryGetValue(id, out var closures) && closures.Contains(date.Date))
            return false;

        return true;
    }

    /// <summary>Shifts a nominal end to the next working day on the default calendar.</summary>
    public DateTime RollForward(DateTime nominal) => RollForward(nominal, DefaultCalendarId);

    /// <summary>
    /// Shifts a nominal end to the next working day on the named jurisdiction's
    /// calendar. Capped at 30 iterations so a badly populated closure table can
    /// never spin forever.
    /// </summary>
    public DateTime RollForward(DateTime nominal, string? calendarId)
    {
        var effective = nominal.Date;
        for (var guard = 0; guard < 30 && !IsWorkingDay(effective, calendarId); guard++)
            effective = effective.AddDays(1);
        return effective;
    }

    /// <summary>
    /// Maps a matter's country code onto a calendar. Unknown jurisdictions fall
    /// back to the default rather than silently borrowing US rules - EP/PCT/CN
    /// all land here today and need their own tables before being relied on.
    /// </summary>
    private string Normalize(string? calendarId)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) return DefaultCalendarId;
        var trimmed = calendarId.Trim();
        return FixedHolidays.ContainsKey(trimmed) ? trimmed : DefaultCalendarId;
    }
}
