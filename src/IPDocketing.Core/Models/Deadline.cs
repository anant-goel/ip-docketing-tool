namespace IPDocketing.Core.Models;

public class Deadline
{
    public int Id { get; set; }
    public int MatterId { get; set; }
    public Matter? Matter { get; set; }

    public int? EventId { get; set; }
    public Event? Event { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>The nominal statutory end (before any non-working-day roll).</summary>
    public DateTime NominalDueDate { get; set; }

    /// <summary>The operative, effective deadline after the roll-forward calendar is
    /// applied. This is the date used everywhere else in the app (sorting, urgency,
    /// alerts) - always shown alongside NominalDueDate so an auditor can see *why*
    /// the operative date moved.</summary>
    public DateTime DueDate { get; set; }

    public DeadlineKind Kind { get; set; } = DeadlineKind.Hard;
    public DeadlineStatus Status { get; set; } = DeadlineStatus.Open;

    public string? ResponsibleUser { get; set; }
    public int ExtensionDaysUsed { get; set; }
    public int? CountryRuleId { get; set; }
    public CountryRule? CountryRule { get; set; }

    /// <summary>Rule version tag captured at calculation time (survives even if the
    /// CountryRule row is later edited/superseded).</summary>
    public string? RuleVersionApplied { get; set; }

    /// <summary>SHA-256 hash of this deadline's calculation payload, chained to the
    /// prior audit record's hash - see AuditService. Lets a malpractice reviewer
    /// recompute the date byte-for-byte and confirm nothing was altered after the fact.</summary>
    public string? AuditHash { get; set; }

    public DateTime? CompletedDate { get; set; }

    public UrgencyLevel GetUrgency(DateTime asOf)
    {
        if (Status == DeadlineStatus.Completed || Status == DeadlineStatus.Waived)
            return UrgencyLevel.Completed;

        var daysLeft = (DueDate.Date - asOf.Date).Days;

        if (daysLeft < 0)
            return UrgencyLevel.Overdue;
        if (daysLeft <= 14)
            return UrgencyLevel.Upcoming;

        return UrgencyLevel.PendingResponse;
    }
}
