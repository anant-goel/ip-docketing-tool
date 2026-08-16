namespace IPDocketing.Core.Models;

public enum MatterType
{
    Patent,
    Trademark,
    Copyright,
    TradeSecret
}

public enum MatterStatus
{
    Pending,
    Active,
    Granted,
    Abandoned,
    Expired,
    Closed
}

public enum EventType
{
    Filing,
    PriorityClaim,
    Publication,
    OfficeAction,
    Response,
    Allowance,
    Grant,
    Renewal,
    Annuity,
    Opposition,
    Abandonment,
    Other
}

public enum DeadlineKind
{
    Hard,
    Soft
}

public enum DeadlineStatus
{
    Open,
    Completed,
    Overdue,
    Extended,
    Waived
}

public enum PtoSource
{
    USPTO,
    EPO,
    WIPO,
    Other
}

public enum UrgencyLevel
{
    Completed,
    PendingResponse,
    Upcoming,
    Overdue
}

/// <summary>
/// Statutory periods are expressed in months far more often than days
/// (e.g. "3 months to respond to an Office Action"). Adding a fixed day
/// count instead of a calendar-correct month offset is a classic source of
/// silent miscalculation across month-end and leap-year boundaries, so the
/// rule engine must know which unit a given rule is defined in.
/// </summary>
public enum PeriodUnit
{
    Days,
    Months
}

/// <summary>Word mark vs device (logo) mark — docx section 6 splits search by this.</summary>
public enum MarkType
{
    Word,
    Device
}

/// <summary>docx section 3: oppositions we filed vs oppositions filed against us.</summary>
public enum OppositionDirection
{
    FiledByUs,
    FiledAgainstUs
}

public enum OppositionStatus
{
    Open,
    NoticeIssued,
    CounterStatementFiled,
    EvidenceStage,
    HearingScheduled,
    Decided,
    Withdrawn,
    Settled
}
