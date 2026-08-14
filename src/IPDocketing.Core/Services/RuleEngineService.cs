using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// Matches a matter's country + matter type + triggering event against the
/// CountryRule table and produces the resulting statutory Deadline.
///
/// Deliberately event-driven and stateless: identical inputs (same event,
/// same rule version, same calendar version) always yield an identical
/// nominal date, effective date, and audit hash, regardless of when the
/// engine runs. Rule resolution (this class) and the non-working-day roll
/// (HolidayCalendarService) are kept isolated from each other, so updating
/// a holiday table can never silently change period math.
/// </summary>
public class RuleEngineService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly HolidayCalendarService _calendar;

    public RuleEngineService(AppDbContext db, AuditService audit, HolidayCalendarService? calendar = null)
    {
        _db = db;
        _audit = audit;
        _calendar = calendar ?? new HolidayCalendarService();
    }

    public Deadline? CalculateAndCreateDeadline(Event triggeringEvent)
    {
        var matter = _db.Matters.Find(triggeringEvent.MatterId);
        if (matter is null) return null;

        // Resolve by (event type, jurisdiction, matter type) and pick the rule
        // version that was actually in force on the triggering date - never
        // "whichever row happens to be in the table" - so a later statutory
        // change never retroactively alters a deadline computed under the
        // rule that applied at the time.
        var rule = _db.CountryRules
            .Where(r => r.CountryCode == matter.Country &&
                        r.MatterType == matter.Type &&
                        r.TriggerEvent == triggeringEvent.Type &&
                        r.EffectiveFrom <= triggeringEvent.EventDate)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault();

        if (rule is null) return null;

        // Calendar-correct period math: DateTime.AddMonths clamps invalid
        // end-of-month results the same way dateutil.relativedelta does
        // (e.g. 31 Jan + 1 month -> 28/29 Feb, never an invalid date), so
        // month-defined statutory periods are never approximated as a fixed
        // day count.
        var nominal = rule.PeriodUnit == PeriodUnit.Months
            ? triggeringEvent.EventDate.Date.AddMonths(rule.PeriodLength)
            : triggeringEvent.EventDate.Date.AddDays(rule.PeriodLength);

        var effective = _calendar.RollForward(nominal);

        var deadline = new Deadline
        {
            MatterId = matter.Id,
            EventId = triggeringEvent.Id,
            Description = rule.DeadlineDescription,
            NominalDueDate = nominal,
            DueDate = effective,
            Kind = DeadlineKind.Hard,
            Status = DeadlineStatus.Open,
            CountryRuleId = rule.Id,
            RuleVersionApplied = rule.RuleVersion
        };

        _db.Deadlines.Add(deadline);
        _db.SaveChanges();

        var auditEntry = _audit.Log("Create", "Deadline", deadline.Id,
            $"Rule {rule.RuleVersion} ({rule.Citation ?? rule.CountryCode + "/" + rule.MatterType}) " +
            $"triggered by {triggeringEvent.Type} on {triggeringEvent.EventDate:yyyy-MM-dd}. " +
            $"Nominal={nominal:yyyy-MM-dd}, Effective={effective:yyyy-MM-dd}, Calendar={_calendar.CalendarVersion}.");

        deadline.AuditHash = auditEntry.RecordHash;
        _db.SaveChanges();

        return deadline;
    }

    /// <summary>
    /// Applies a statutory extension to a deadline's effective due date, capped
    /// at the rule's MaxExtensionDays, and logs the change to the hash-chained
    /// audit trail. Discretionary extensions require an explicit call like this
    /// one rather than ever being silently auto-applied.
    /// </summary>
    public bool TryExtend(int deadlineId, int extraDays, out string message)
    {
        var deadline = _db.Deadlines.Include(d => d.CountryRule).FirstOrDefault(d => d.Id == deadlineId);
        if (deadline is null)
        {
            message = "Deadline not found.";
            return false;
        }

        var maxDays = deadline.CountryRule?.MaxExtensionDays ?? 0;
        var extensionAvailable = deadline.CountryRule?.ExtensionAvailable ?? false;

        if (!extensionAvailable)
        {
            message = "No statutory extension is available for this deadline type/jurisdiction.";
            return false;
        }

        if (deadline.ExtensionDaysUsed + extraDays > maxDays)
        {
            message = $"Requested extension exceeds the maximum of {maxDays} days for this rule.";
            return false;
        }

        deadline.DueDate = deadline.DueDate.AddDays(extraDays);
        deadline.ExtensionDaysUsed += extraDays;
        deadline.Status = DeadlineStatus.Extended;
        _db.SaveChanges();

        var auditEntry = _audit.Log("Extend", "Deadline", deadline.Id,
            $"Extended by {extraDays} day(s). New effective due date: {deadline.DueDate:yyyy-MM-dd}.");
        deadline.AuditHash = auditEntry.RecordHash;
        _db.SaveChanges();

        message = "Extension applied.";
        return true;
    }
}
