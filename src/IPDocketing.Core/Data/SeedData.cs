using IPDocketing.Core.Models;

namespace IPDocketing.Core.Data;

/// <summary>
/// Populates a fresh local database with a starter rule registry and a
/// couple of sample matters, so the app is usable immediately after first
/// launch. Rules are seeded with real statutory citations and month-based
/// periods (see the "Regulatory & Statutory Baseline" reference this engine
/// follows) - extend CountryRules to add more jurisdictions and event types.
/// </summary>
public static class SeedData
{
    public static void EnsureSeeded(AppDbContext db)
    {
        db.Database.EnsureCreated();

        if (db.CountryRules.Any())
            return;

        var rules = new List<CountryRule>
        {
            new() {
                CountryCode = "US", CountryName = "United States", MatterType = MatterType.Patent,
                TriggerEvent = EventType.OfficeAction, DeadlineDescription = "Non-final OA response (US)",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 3,
                ExtensionAvailable = true, MaxExtensionDays = 90,
                Citation = "37 CFR 1.134 / 1.136(a)", CitationUrl = "https://www.ecfr.gov/current/title-37/section-1.134",
                EffectiveFrom = new DateTime(2013, 3, 19), RuleVersion = "USPTO_37CFR_1.134_v2024.1",
                ExtensionFeeNote = "37 CFR 1.136(a) - escalating extension fees per month, statutory maximum 6 months total" },

            new() {
                CountryCode = "US", CountryName = "United States", MatterType = MatterType.Patent,
                TriggerEvent = EventType.Allowance, DeadlineDescription = "Issue fee payment (US)",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 3,
                ExtensionAvailable = false, MaxExtensionDays = 0,
                Citation = "37 CFR 1.311", EffectiveFrom = new DateTime(2000, 1, 1), RuleVersion = "USPTO_37CFR_1.311_v1" },

            new() {
                CountryCode = "US", CountryName = "United States", MatterType = MatterType.Trademark,
                TriggerEvent = EventType.OfficeAction, DeadlineDescription = "TM Office Action response (US)",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 3,
                ExtensionAvailable = false, MaxExtensionDays = 0,
                Citation = "15 U.S.C. 1062(b)", EffectiveFrom = new DateTime(2022, 12, 1), RuleVersion = "USPTO_TMA_2022_v1",
                ExtensionFeeNote = "Post Trademark Modernization Act rule change - no automatic extension" },

            new() {
                CountryCode = "EP", CountryName = "European Patent Office", MatterType = MatterType.Patent,
                TriggerEvent = EventType.OfficeAction, DeadlineDescription = "EPO examination report response",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 4,
                ExtensionAvailable = true, MaxExtensionDays = 60,
                Citation = "EPC Rule 71", CitationUrl = "https://www.epo.org/en/legal/guidelines-epc",
                EffectiveFrom = new DateTime(2023, 11, 1), RuleVersion = "EPO_R71_v2023.11",
                ExtensionFeeNote = "Further processing under Art. 121 available on missed periods; one extension typically available under Rule 132" },

            new() {
                CountryCode = "EP", CountryName = "European Patent Office", MatterType = MatterType.Patent,
                TriggerEvent = EventType.Publication, DeadlineDescription = "EP validation deadline",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 3,
                ExtensionAvailable = false, MaxExtensionDays = 0,
                Citation = "EPC Rule 134", EffectiveFrom = new DateTime(2000, 1, 1), RuleVersion = "EPO_R134_v1" },

            new() {
                CountryCode = "PCT", CountryName = "PCT (WIPO)", MatterType = MatterType.Patent,
                TriggerEvent = EventType.PriorityClaim, DeadlineDescription = "National phase entry (30 months)",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 30,
                ExtensionAvailable = false, MaxExtensionDays = 0,
                Citation = "PCT Article 22", CitationUrl = "https://www.wipo.int/pct/en/texts/articles/a22.html",
                EffectiveFrom = new DateTime(2000, 1, 1), RuleVersion = "PCT_ART22_v1",
                ExtensionFeeNote = "Some national offices allow 31 months (Art. 39, where a Ch. II demand applies); verify per-state rule before relying on the 30-month default. Rolls against the national office of entry's calendar, not WIPO's (PCT Rule 80.5)." },

            new() {
                CountryCode = "CN", CountryName = "China (CNIPA)", MatterType = MatterType.Patent,
                TriggerEvent = EventType.OfficeAction, DeadlineDescription = "CNIPA OA response",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 4,
                ExtensionAvailable = true, MaxExtensionDays = 30,
                Citation = "CNIPA Patent Examination Guidelines", EffectiveFrom = new DateTime(2000, 1, 1), RuleVersion = "CNIPA_v1" },

            new() {
                CountryCode = "US", CountryName = "United States", MatterType = MatterType.Patent,
                TriggerEvent = EventType.Grant, DeadlineDescription = "1st maintenance fee window opens (3.5yr)",
                PeriodUnit = PeriodUnit.Months, PeriodLength = 42,
                ExtensionAvailable = true, MaxExtensionDays = 180,
                Citation = "35 U.S.C. 41(b) / 37 CFR 1.362", EffectiveFrom = new DateTime(2000, 1, 1), RuleVersion = "USPTO_MAINT_v1",
                ExtensionFeeNote = "Surcharge window: 3.5-4.0 years from grant" },
        };

        db.CountryRules.AddRange(rules);
        db.SaveChanges();

        // A couple of illustrative matters so the dashboard is not empty on first run.
        var parent = new Matter
        {
            MatterNumber = "ACME-P-0001",
            Title = "Modular Battery Enclosure",
            ClientName = "Acme Robotics Inc.",
            Type = MatterType.Patent,
            Country = "US",
            Status = MatterStatus.Active,
            FilingDate = DateTime.UtcNow.AddMonths(-8),
            ApplicationNumber = "17/123,456",
            CreatedDate = DateTime.UtcNow
        };
        db.Matters.Add(parent);
        db.SaveChanges();

        var child = new Matter
        {
            MatterNumber = "ACME-P-0001-EP",
            Title = "Modular Battery Enclosure (EP counterpart)",
            ClientName = "Acme Robotics Inc.",
            Type = MatterType.Patent,
            Country = "EP",
            Status = MatterStatus.Pending,
            FilingDate = DateTime.UtcNow.AddMonths(-3),
            ParentMatterId = parent.Id,
            CreatedDate = DateTime.UtcNow
        };
        db.Matters.Add(child);

        var tm = new Matter
        {
            MatterNumber = "ACME-TM-0007",
            Title = "ACME word mark, Class 9",
            ClientName = "Acme Robotics Inc.",
            Type = MatterType.Trademark,
            Country = "US",
            Status = MatterStatus.Active,
            FilingDate = DateTime.UtcNow.AddMonths(-14),
            CreatedDate = DateTime.UtcNow
        };
        db.Matters.Add(tm);
        db.SaveChanges();

        var oaEvent = new Event
        {
            MatterId = parent.Id,
            Type = EventType.OfficeAction,
            EventDate = DateTime.UtcNow.AddDays(-15),
            Notes = "Non-final rejection received (USPTO)"
        };
        db.Events.Add(oaEvent);
        db.SaveChanges();

        var oaNominal = oaEvent.EventDate.Date.AddMonths(3);
        var calendar = new Services.HolidayCalendarService();
        var oaEffective = calendar.RollForward(oaNominal);

        db.Deadlines.Add(new Deadline
        {
            MatterId = parent.Id,
            EventId = oaEvent.Id,
            Description = "Respond to non-final Office Action",
            NominalDueDate = oaNominal,
            DueDate = oaEffective,
            Kind = DeadlineKind.Hard,
            Status = DeadlineStatus.Open,
            ResponsibleUser = "J. Patel",
            CountryRuleId = rules[0].Id,
            RuleVersionApplied = rules[0].RuleVersion
        });

        var tmNominal = DateTime.UtcNow.AddDays(6).Date;
        db.Deadlines.Add(new Deadline
        {
            MatterId = tm.Id,
            Description = "File Statement of Use",
            NominalDueDate = tmNominal,
            DueDate = calendar.RollForward(tmNominal),
            Kind = DeadlineKind.Hard,
            Status = DeadlineStatus.Open,
            ResponsibleUser = "M. Chen"
        });

        var epNominal = DateTime.UtcNow.AddDays(-3).Date;
        db.Deadlines.Add(new Deadline
        {
            MatterId = child.Id,
            Description = "EP validation / national phase review",
            NominalDueDate = epNominal,
            DueDate = calendar.RollForward(epNominal),
            Kind = DeadlineKind.Hard,
            Status = DeadlineStatus.Open,
            ResponsibleUser = "J. Patel"
        });

        var mChen = new TeamMember { Name = "M. Chen", Role = "Attorney" };
        var jPatel = new TeamMember { Name = "J. Patel", Role = "Paralegal" };
        db.TeamMembers.AddRange(mChen, jPatel);
        db.SaveChanges();

        tm.AssignedToId = mChen.Id;
        child.AssignedToId = jPatel.Id;

        db.SaveChanges();
    }
}
