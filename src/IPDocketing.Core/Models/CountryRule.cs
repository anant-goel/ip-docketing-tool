namespace IPDocketing.Core.Models;

/// <summary>
/// A single statutory-deadline rule: given a matter type, jurisdiction, and
/// triggering event, defines the period after the trigger the deadline
/// falls due, and whether extensions are available.
///
/// Base date, period length/unit, and (implicitly, via the roll calendar
/// applied in RuleEngineService) the holiday roll are treated as three
/// independent inputs, per the "bind all three at rule-resolution time"
/// principle - never infer a base date or roll calendar from jurisdiction
/// alone. Each rule is version-pinned and carries its citation so a
/// computed deadline can be defended years later.
/// </summary>
public class CountryRule
{
    public int Id { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public MatterType MatterType { get; set; }
    public EventType TriggerEvent { get; set; }

    public string DeadlineDescription { get; set; } = string.Empty;

    /// <summary>Period unit. Statutory periods are usually Months (e.g. "3 months to
    /// respond to an OA") - use Months so calendar-correct arithmetic (DateTime.AddMonths,
    /// which clamps invalid end-of-month dates the same way relativedelta does) is applied
    /// instead of an inexact fixed day count.</summary>
    public PeriodUnit PeriodUnit { get; set; } = PeriodUnit.Months;
    public int PeriodLength { get; set; }

    public bool ExtensionAvailable { get; set; }
    public int MaxExtensionDays { get; set; }
    public string? ExtensionFeeNote { get; set; }

    /// <summary>Governing statute/rule citation (e.g. "37 CFR 1.134"), shown in the
    /// audit trail so a computed deadline is traceable to its legal source.</summary>
    public string? Citation { get; set; }
    public string? CitationUrl { get; set; }

    /// <summary>Date this rule version takes effect. Old and new versions of a rule
    /// can coexist in the registry; resolution selects by the triggering event's date,
    /// not "whichever row happens to be in the table" - so a mid-year statutory change
    /// (e.g. the EPO's 1 Nov 2023 notification-date change) never retroactively alters
    /// a deadline that was already computed under the prior rule.</summary>
    public DateTime EffectiveFrom { get; set; } = new(2000, 1, 1);

    /// <summary>Free-form version tag stored on every Deadline computed from this rule,
    /// so the audit trail records exactly which rule revision produced a given date.</summary>
    public string RuleVersion { get; set; } = "v1";
}
