using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// Renewal docketing - the largest gap in the app until now.
///
/// A registered Indian trademark lasts ten years from the date of application
/// and is renewable indefinitely for ten years at a time (Trade Marks Act 1999,
/// s.25(1)-(2)). Missing a renewal is the single most common - and most
/// expensive - malpractice event in trademark practice, because unlike an
/// examination response there is no reminder from anyone: the Registry's
/// notice under s.25(3) goes to the proprietor's address on record, which for
/// an agent-filed mark is often stale.
///
/// The statute gives four separate dates, and docketing only the last one is
/// how firms lose marks. All four are generated here:
///
///   1. Renewal window opens    - one year before expiry (Rule 57 permits
///                                filing TM-R within one year before expiry)
///   2. Renewal due             - the expiry date itself
///   3. Late renewal with fee   - six months after expiry, on payment of the
///                                surcharge (s.25(4))
///   4. Restoration window ends - twelve months after expiry; restoration
///                                under s.25(4) via TM-R with Form TM-18
///                                surcharge. After this the mark is gone.
///
/// IMPORTANT - the ten years runs from the DATE OF APPLICATION, not the date
/// the certificate issued. This trips people up constantly, because Indian
/// registration certificates are frequently granted years after filing and the
/// certificate's own date is not the anchor. Where a filing date is present it
/// is used; registration date is only a fallback, and when that happens the
/// deadline description says so explicitly rather than quietly producing a date
/// that could be years wrong.
///
/// Nothing here rolls dates for holidays by itself - it hands each nominal date
/// to <see cref="HolidayCalendarService"/> exactly like RuleEngineService does,
/// so renewal dates carry the same operative/nominal pair and the same audit
/// trail as every other deadline.
/// </summary>
public class RenewalService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly HolidayCalendarService _calendar;

    /// <summary>Marker written into RuleVersionApplied so renewal deadlines are identifiable.</summary>
    public const string RuleVersion = "IN_TMACT1999_S25_v2017";

    private const string WindowOpensPrefix = "Renewal window opens";
    private const string RenewalDuePrefix = "Renewal due";
    private const string LateRenewalPrefix = "Late renewal closes";
    private const string RestorationPrefix = "Restoration window closes";

    public RenewalService(AppDbContext db, AuditService audit, HolidayCalendarService calendar)
    {
        _db = db;
        _audit = audit;
        _calendar = calendar;
    }

    public sealed record RenewalSchedule(
        DateTime AnchorDate,
        bool AnchoredOnFilingDate,
        DateTime ExpiryDate,
        DateTime WindowOpens,
        DateTime LateRenewalCloses,
        DateTime RestorationCloses)
    {
        public int DaysToExpiry(DateTime asOf) => (ExpiryDate.Date - asOf.Date).Days;

        /// <summary>True once the mark is past restoration - nothing can be done.</summary>
        public bool IsIrrecoverable(DateTime asOf) => asOf.Date > RestorationCloses.Date;
    }

    /// <summary>
    /// Computes the four dates for one matter, or null when there is nothing to
    /// anchor on. Deliberately returns null rather than guessing: a renewal date
    /// invented from no anchor is worse than no renewal date, because it looks
    /// authoritative on a dashboard.
    /// </summary>
    public RenewalSchedule? BuildSchedule(Matter matter, int termYears = 10)
    {
        var anchoredOnFiling = matter.FilingDate is not null;
        var anchor = matter.FilingDate ?? matter.RegistrationDate;
        if (anchor is null) return null;

        // If a renewal has already happened, the current term runs from the last
        // renewal, not the original filing. RenewalDueDate is treated as the
        // authoritative current expiry when it is later than the computed one.
        var computedExpiry = anchor.Value.Date.AddYears(termYears);
        var expiry = matter.RenewalDueDate is { } stored && stored.Date > computedExpiry
            ? stored.Date
            : computedExpiry;

        return new RenewalSchedule(
            AnchorDate: anchor.Value.Date,
            AnchoredOnFilingDate: anchoredOnFiling,
            ExpiryDate: expiry,
            WindowOpens: expiry.AddYears(-1),
            LateRenewalCloses: expiry.AddMonths(6),
            RestorationCloses: expiry.AddMonths(12));
    }

    public sealed record DocketResult(int MattersProcessed, int DeadlinesCreated, int Skipped, List<string> Notes);

    /// <summary>
    /// Dockets renewal deadlines across the portfolio. Idempotent: a deadline
    /// whose description already exists for that matter at that date is not
    /// duplicated, so this is safe to run on every startup and safe to run
    /// twice by hand. Only registered/active trademarks are considered -
    /// docketing a renewal for a mark still under examination would be noise.
    /// </summary>
    public DocketResult DocketRenewals(int? singleMatterId = null)
    {
        var notes = new List<string>();
        var created = 0;
        var skipped = 0;

        var query = _db.Matters
            .Where(m => m.Type == MatterType.Trademark)
            .AsQueryable();

        if (singleMatterId is not null)
            query = query.Where(m => m.Id == singleMatterId.Value);
        else
            query = query.Where(m =>
                m.Status == MatterStatus.Granted ||
                m.Status == MatterStatus.Active ||
                m.Status == MatterStatus.Expired);

        var matters = query.ToList();

        foreach (var matter in matters)
        {
            var schedule = BuildSchedule(matter);
            if (schedule is null)
            {
                skipped++;
                notes.Add($"{matter.MatterNumber}: no filing or registration date on record - cannot anchor a renewal term.");
                continue;
            }

            if (!schedule.AnchoredOnFilingDate)
                notes.Add($"{matter.MatterNumber}: no filing date, so the term was anchored on the registration date. " +
                          "Section 25 runs from the date of application - confirm the filing date and re-run.");

            // Keep the matter's own renewal field in step, so the register view
            // and the status sheet agree with the docket.
            if (matter.RenewalDueDate?.Date != schedule.ExpiryDate)
            {
                matter.RenewalDueDate = schedule.ExpiryDate;
                _db.Matters.Update(matter);
            }

            var existing = _db.Deadlines
                .Where(d => d.MatterId == matter.Id)
                .ToList();

            created += EnsureDeadline(matter, existing,
                $"{WindowOpensPrefix} (file TM-R from this date)", schedule.WindowOpens, DeadlineKind.Soft);

            created += EnsureDeadline(matter, existing,
                $"{RenewalDuePrefix} — 10-year term expires (s.25)", schedule.ExpiryDate, DeadlineKind.Hard);

            created += EnsureDeadline(matter, existing,
                $"{LateRenewalPrefix} — 6 months post-expiry, surcharge payable (s.25(4))",
                schedule.LateRenewalCloses, DeadlineKind.Hard);

            created += EnsureDeadline(matter, existing,
                $"{RestorationPrefix} — 12 months post-expiry; mark is lost after this",
                schedule.RestorationCloses, DeadlineKind.Hard);
        }

        if (created > 0 || matters.Count > 0) _db.SaveChanges();

        if (created > 0)
            _audit.Log("Docket", "Renewal", singleMatterId ?? 0,
                $"Docketed {created} renewal deadline(s) across {matters.Count} trademark(s) under {RuleVersion}.");

        return new DocketResult(matters.Count, created, skipped, notes);
    }

    private int EnsureDeadline(Matter matter, List<Deadline> existing, string description,
                               DateTime nominal, DeadlineKind kind)
    {
        // Match on the stable prefix rather than the full text, so wording
        // changes in a future version don't produce a second copy of a deadline
        // the user has already actioned.
        var prefix = description.Split('—')[0].Trim();
        if (existing.Any(d => d.Description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return 0;

        var effective = _calendar.RollForward(nominal, matter.Country);

        var deadline = new Deadline
        {
            MatterId = matter.Id,
            Description = description,
            NominalDueDate = nominal,
            DueDate = effective,
            Kind = kind,
            Status = DeadlineStatus.Open,
            RuleVersionApplied = RuleVersion,
            ResponsibleUser = matter.AttorneyOfRecord
        };

        _db.Deadlines.Add(deadline);
        existing.Add(deadline);
        return 1;
    }

    /// <summary>
    /// Marks a renewal as done and rolls the term forward another ten years,
    /// docketing the next cycle. This is the operation that actually keeps a
    /// portfolio alive across decades rather than needing manual re-entry every
    /// renewal.
    /// </summary>
    public void RecordRenewal(int matterId, DateTime renewedOn, int termYears = 10)
    {
        var matter = _db.Matters.FirstOrDefault(m => m.Id == matterId);
        if (matter is null) return;

        var schedule = BuildSchedule(matter, termYears);
        var previousExpiry = schedule?.ExpiryDate ?? renewedOn.Date;

        // The new term runs from the previous expiry, not from the payment
        // date - paying early does not shorten the next term.
        matter.RenewalDueDate = previousExpiry.AddYears(termYears);
        matter.Status = MatterStatus.Active;
        _db.Matters.Update(matter);

        // Close out the deadlines for the term just renewed.
        var openRenewalDeadlines = _db.Deadlines
            .Where(d => d.MatterId == matterId &&
                        d.RuleVersionApplied == RuleVersion &&
                        (d.Status == DeadlineStatus.Open || d.Status == DeadlineStatus.Extended))
            .ToList();

        foreach (var deadline in openRenewalDeadlines)
        {
            deadline.Status = DeadlineStatus.Completed;
            deadline.CompletedDate = renewedOn;
        }

        _db.SaveChanges();

        _audit.Log("Renew", "Matter", matterId,
            $"Renewed on {renewedOn:yyyy-MM-dd}. Next expiry {matter.RenewalDueDate:yyyy-MM-dd}. " +
            $"{openRenewalDeadlines.Count} renewal deadline(s) closed.");

        DocketRenewals(matterId);
    }

    public sealed record RenewalRow(
        int MatterId,
        string MatterNumber,
        string Title,
        string ClientName,
        DateTime ExpiryDate,
        int DaysRemaining,
        string Stage,
        string? AttorneyOfRecord);

    /// <summary>
    /// The renewal watchlist: everything expiring, in its late window, or in
    /// restoration, ordered by urgency. Drives the renewals view.
    /// </summary>
    public List<RenewalRow> GetWatchlist(int horizonDays = 400, DateTime? asOf = null)
    {
        var today = (asOf ?? DateTime.Today).Date;
        var rows = new List<RenewalRow>();

        var matters = _db.Matters
            .Include(m => m.AssignedTo)
            .Where(m => m.Type == MatterType.Trademark)
            .ToList();

        foreach (var matter in matters)
        {
            var schedule = BuildSchedule(matter);
            if (schedule is null) continue;

            var days = schedule.DaysToExpiry(today);
            if (days > horizonDays) continue;

            var stage = today <= schedule.ExpiryDate
                ? (today >= schedule.WindowOpens ? "Renewable now" : "Upcoming")
                : today <= schedule.LateRenewalCloses ? "Late — surcharge payable"
                : today <= schedule.RestorationCloses ? "Restoration only"
                : "Lapsed — beyond restoration";

            rows.Add(new RenewalRow(
                matter.Id, matter.MatterNumber, matter.Title, matter.ClientName,
                schedule.ExpiryDate, days, stage, matter.AttorneyOfRecord));
        }

        return rows.OrderBy(r => r.ExpiryDate).ToList();
    }
}
