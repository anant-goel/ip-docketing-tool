using System.Text;
using IPDocketing.Core.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.Core.Services;

/// <summary>
/// Bulk portfolio import and export.
///
/// This is the feature that decides whether the app gets used at all. Nobody
/// re-types four hundred marks into a new tool by hand, so a docketing system
/// without an importer is a docketing system with an empty database. Equally,
/// an app you cannot get data *out* of is one no sensible person commits a
/// portfolio to - the export exists so this is never a one-way door.
///
/// The import is deliberately two-phase. <see cref="Validate"/> parses and
/// reports without writing anything; <see cref="Import"/> commits. A silent
/// bulk import that half-succeeds on a malformed spreadsheet is far worse than
/// one that refuses and says which row is wrong, because the damage is spread
/// across hundreds of records nobody will re-check.
///
/// Matching is by application number first, then matter number. A row matching
/// an existing matter updates it rather than creating a duplicate - re-importing
/// a corrected sheet is a normal thing to want to do.
/// </summary>
public class PortfolioImportService
{
    private readonly AppDbContext _db;
    private readonly MatterService _matters;
    private readonly AuditService _audit;
    private readonly MarkSimilarityService _similarity = new();

    public PortfolioImportService(AppDbContext db, MatterService matters, AuditService audit)
    {
        _db = db;
        _matters = matters;
        _audit = audit;
    }

    /// <summary>Column headers the importer understands, in the order the template emits them.</summary>
    public static readonly string[] TemplateColumns =
    {
        "MatterNumber", "ApplicationNumber", "RegistrationNumber", "Mark", "Client",
        "Proprietor", "Class", "MarkType", "Status", "Country", "State",
        "AttorneyOfRecord", "AttorneyCode", "FilingDate", "RegistrationDate",
        "RenewalDueDate", "PortalAlert"
    };

    public sealed record RowIssue(int LineNumber, string Column, string Message, bool IsFatal);

    public sealed record ParsedRow(int LineNumber, Matter Matter, bool IsUpdate, int? ExistingId);

    public sealed record ValidationReport(
        List<ParsedRow> Rows,
        List<RowIssue> Issues,
        int NewCount,
        int UpdateCount)
    {
        public bool HasFatalIssues => Issues.Any(i => i.IsFatal);
        public int WarningCount => Issues.Count(i => !i.IsFatal);
    }

    /// <summary>
    /// Parses CSV text and reports what would happen. Writes nothing.
    /// Accepts the columns in any order, matched case-insensitively by header
    /// name, so a sheet exported from another system can usually be imported
    /// after renaming headers rather than reordering columns.
    /// </summary>
    public ValidationReport Validate(string csvText)
    {
        var rows = new List<ParsedRow>();
        var issues = new List<RowIssue>();

        var lines = SplitLines(csvText);
        if (lines.Count == 0)
        {
            issues.Add(new RowIssue(0, "", "The file is empty.", true));
            return new ValidationReport(rows, issues, 0, 0);
        }

        var headers = ParseCsvLine(lines[0])
            .Select(h => h.Trim().Replace(" ", "").Replace("_", ""))
            .ToList();

        int IndexOf(params string[] names)
        {
            foreach (var name in names)
            {
                var i = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        var idxMatter = IndexOf("MatterNumber", "MatterNo", "Reference", "OurRef");
        var idxApp = IndexOf("ApplicationNumber", "ApplicationNo", "AppNo", "TMNumber", "TradeMarkNo");
        var idxReg = IndexOf("RegistrationNumber", "RegistrationNo", "RegNo");
        var idxMark = IndexOf("Mark", "Title", "TradeMark", "WordMark", "BrandName");
        var idxClient = IndexOf("Client", "ClientName");
        var idxProprietor = IndexOf("Proprietor", "ProprietorName", "Applicant", "Owner");
        var idxClass = IndexOf("Class", "NiceClass", "TMClass");
        var idxMarkType = IndexOf("MarkType", "Type");
        var idxStatus = IndexOf("Status");
        var idxCountry = IndexOf("Country", "Jurisdiction");
        var idxState = IndexOf("State");
        var idxAttorney = IndexOf("AttorneyOfRecord", "Attorney", "Agent", "AgentName");
        var idxAttorneyCode = IndexOf("AttorneyCode", "AgentCode", "AgentRegistrationNo");
        var idxFiling = IndexOf("FilingDate", "DateOfApplication", "ApplicationDate");
        var idxRegDate = IndexOf("RegistrationDate", "DateOfRegistration");
        var idxRenewal = IndexOf("RenewalDueDate", "RenewalDue", "ValidUpto", "ValidUpTo");
        var idxAlert = IndexOf("PortalAlert", "Alert", "Remarks");

        // A mark column is preferred but not required - the Filed Applications
        // listing has no mark column at all, only application numbers. Those
        // rows still make perfectly good docket records; the name gets filled
        // in from the e-Status page later.
        if (idxMark < 0 && idxApp < 0)
        {
            issues.Add(new RowIssue(1, "Mark",
                "No column for the mark and none for the application number. One of the two is needed " +
                "to identify a record. Mark aliases: Mark, Title, TradeMark, WordMark, BrandName. " +
                "Application number aliases: ApplicationNumber, AppNo, TMNumber, Form/Application Number.", true));
            return new ValidationReport(rows, issues, 0, 0);
        }

        if (idxMark < 0)
            issues.Add(new RowIssue(1, "Mark",
                "No mark column found, so each row is recorded under its application number and flagged " +
                "for the name to be filled in. Run a Guided e-Status pass afterwards to pull the marks.", false));

        // Loaded once rather than per row - a 500-row import otherwise runs
        // 1,000 queries and takes visibly long enough to look broken.
        var existingMatters = _db.Matters.ToList();
        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < lines.Count; i++)
        {
            var lineNumber = i + 1;
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var cells = ParseCsvLine(raw);
            string? Cell(int index) =>
                index >= 0 && index < cells.Count && !string.IsNullOrWhiteSpace(cells[index])
                    ? cells[index].Trim()
                    : null;

            var applicationNumber = Cell(idxApp);
            var mark = Cell(idxMark);

            if (mark is null)
            {
                if (applicationNumber is null)
                {
                    issues.Add(new RowIssue(lineNumber, "Mark",
                        "Neither a mark nor an application number on this row - skipped.", false));
                    continue;
                }

                // Placeholder title, deliberately obvious rather than blank, so
                // these rows are findable and clearly incomplete rather than
                // looking like a mark genuinely named after a number.
                mark = $"[Unnamed - app {applicationNumber}]";
            }
            var matterNumber = Cell(idxMatter);


            // Duplicate detection inside the file itself, which is a far more
            // common problem than duplicates against the database.
            var fileKey = applicationNumber ?? matterNumber ?? mark;
            if (!seenInFile.Add(fileKey))
                issues.Add(new RowIssue(lineNumber, "ApplicationNumber",
                    $"'{fileKey}' appears more than once in this file - the last occurrence wins.", false));

            // Exact identifier match first - an application number is the one
            // truly unique key here.
            var existing = existingMatters.FirstOrDefault(m =>
                (applicationNumber is not null && string.Equals(m.ApplicationNumber, applicationNumber, StringComparison.OrdinalIgnoreCase)) ||
                (matterNumber is not null && string.Equals(m.MatterNumber, matterNumber, StringComparison.OrdinalIgnoreCase)));

            // Phase 35: where there is no identifier match, look for a probable
            // duplicate on the mark itself. Portfolios routinely arrive without
            // application numbers, or with them formatted differently between
            // systems, and importing the same mark twice under two references
            // is a mess to unpick later - two sets of renewal deadlines, two
            // status histories, and no way to tell which is authoritative.
            //
            // This only WARNS. It never silently merges: a near-identical name
            // in a different class is often a genuinely separate registration,
            // and quietly collapsing two real matters into one would be far
            // worse than a duplicate the user can see and delete.
            if (existing is null && applicationNumber is null)
            {
                var probable = existingMatters
                    .Where(m => m.Type == MatterType.Trademark)
                    .Select(m => (Matter: m, Result: _similarity.Compare(mark, m.Title)))
                    .Where(x => x.Result.Score >= 90)
                    .OrderByDescending(x => x.Result.Score)
                    .FirstOrDefault();

                if (probable.Matter is not null)
                    issues.Add(new RowIssue(lineNumber, "Mark",
                        $"'{mark}' looks like the existing matter {probable.Matter.MatterNumber} " +
                        $"('{probable.Matter.Title}', {probable.Result.Score}% - {probable.Result.Reasons.FirstOrDefault()}). " +
                        "It has no application number to match on, so it will be imported as a NEW matter. " +
                        "Add the application number to link them instead.", false));
            }

            var matter = existing is null ? new Matter() : CloneForUpdate(existing);

            matter.Title = mark;
            matter.MatterNumber = matterNumber
                ?? existing?.MatterNumber
                ?? (applicationNumber is not null ? $"TM-{applicationNumber}" : $"TM-IMP-{lineNumber}");
            matter.ApplicationNumber = applicationNumber ?? matter.ApplicationNumber;
            matter.GrantNumber = Cell(idxReg) ?? matter.GrantNumber;
            matter.ClientName = Cell(idxClient) ?? matter.ClientName ?? "Unassigned client";
            matter.ProprietorName = Cell(idxProprietor) ?? matter.ProprietorName;
            matter.NiceClass = Cell(idxClass) ?? matter.NiceClass;
            matter.State = Cell(idxState) ?? matter.State;
            matter.AttorneyOfRecord = Cell(idxAttorney) ?? matter.AttorneyOfRecord;
            matter.AttorneyCode = Cell(idxAttorneyCode) ?? matter.AttorneyCode;
            matter.PortalAlert = Cell(idxAlert) ?? matter.PortalAlert;
            matter.Country = Cell(idxCountry) ?? matter.Country ?? "IN";
            matter.Type = MatterType.Trademark;

            var markTypeText = Cell(idxMarkType);
            if (markTypeText is not null)
            {
                if (Enum.TryParse<MarkType>(markTypeText, true, out var markType)) matter.MarkType = markType;
                else if (markTypeText.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                         markTypeText.Contains("logo", StringComparison.OrdinalIgnoreCase)) matter.MarkType = MarkType.Device;
                else if (markTypeText.Contains("word", StringComparison.OrdinalIgnoreCase)) matter.MarkType = MarkType.Word;
                else issues.Add(new RowIssue(lineNumber, "MarkType",
                    $"'{markTypeText}' isn't a mark type I recognise - left unset.", false));
            }

            var statusText = Cell(idxStatus);
            if (statusText is not null)
            {
                var mapped = MapStatus(statusText);
                if (mapped is not null) matter.Status = mapped.Value;
                else issues.Add(new RowIssue(lineNumber, "Status",
                    $"'{statusText}' didn't map to a known status - left as {matter.Status}.", false));
            }

            matter.FilingDate = ParseDate(Cell(idxFiling), lineNumber, "FilingDate", issues) ?? matter.FilingDate;
            matter.RegistrationDate = ParseDate(Cell(idxRegDate), lineNumber, "RegistrationDate", issues) ?? matter.RegistrationDate;
            matter.RenewalDueDate = ParseDate(Cell(idxRenewal), lineNumber, "RenewalDueDate", issues) ?? matter.RenewalDueDate;

            if (matter.FilingDate is null && matter.RegistrationDate is null)
                issues.Add(new RowIssue(lineNumber, "FilingDate",
                    "No filing or registration date - no renewal term can be computed for this mark.", false));

            if (matter.FilingDate > DateTime.Today)
                issues.Add(new RowIssue(lineNumber, "FilingDate",
                    $"Filing date {matter.FilingDate:yyyy-MM-dd} is in the future - check the day/month order.", false));

            rows.Add(new ParsedRow(lineNumber, matter, existing is not null, existing?.Id));
        }

        return new ValidationReport(
            rows, issues,
            rows.Count(r => !r.IsUpdate),
            rows.Count(r => r.IsUpdate));
    }

    /// <summary>
    /// Commits a validated report. Refuses outright if validation found a fatal
    /// issue - importing "most of" a portfolio leaves you worse off than not
    /// importing, because you cannot tell afterwards which rows are missing.
    /// </summary>
    public (int Created, int Updated) Import(ValidationReport report)
    {
        if (report.HasFatalIssues)
            throw new InvalidOperationException(
                "This file has errors that must be fixed before it can be imported.");

        var created = 0;
        var updated = 0;

        foreach (var row in report.Rows)
        {
            if (row.IsUpdate && row.ExistingId is { } id)
            {
                var target = _db.Matters.FirstOrDefault(m => m.Id == id);
                if (target is null) continue;
                CopyInto(row.Matter, target);
                _db.Matters.Update(target);
                updated++;
            }
            else
            {
                _db.Matters.Add(row.Matter);
                created++;
            }
        }

        _db.SaveChanges();
        _audit.Log("Import", "Matter", 0,
            $"Bulk import: {created} matter(s) created, {updated} updated, {report.WarningCount} warning(s).");

        return (created, updated);
    }

    /// <summary>Full portfolio export, using the same column set the importer reads.</summary>
    public string ExportCsv(IEnumerable<Matter>? matters = null)
    {
        var source = (matters ?? _matters.GetAll()).ToList();
        var sb = new StringBuilder(string.Join(',', TemplateColumns)).Append('\n');

        foreach (var m in source)
        {
            sb.AppendLine(string.Join(',',
                Csv(m.MatterNumber), Csv(m.ApplicationNumber), Csv(m.GrantNumber), Csv(m.Title),
                Csv(m.ClientName), Csv(m.ProprietorName), Csv(m.NiceClass), Csv(m.MarkType?.ToString()),
                Csv(m.Status.ToString()), Csv(m.Country), Csv(m.State),
                Csv(m.AttorneyOfRecord), Csv(m.AttorneyCode),
                Csv(m.FilingDate?.ToString("yyyy-MM-dd")),
                Csv(m.RegistrationDate?.ToString("yyyy-MM-dd")),
                Csv(m.RenewalDueDate?.ToString("yyyy-MM-dd")),
                Csv(m.PortalAlert)));
        }

        return sb.ToString();
    }

    /// <summary>An empty sheet with one worked example, so the expected shape is obvious.</summary>
    public string BuildTemplateCsv() =>
        string.Join(',', TemplateColumns) + "\n" +
        "TM-2024-001,4567890,,SHUBH LAXMI,Acme Foods Pvt Ltd,Acme Foods Pvt Ltd,29,Word,Granted,IN," +
        "Himachal Pradesh,R. Sharma,,2019-04-12,2021-08-30,,\n";

    // --- helpers ---

    /// <summary>
    /// Maps the free-text status wording the TMR portal and most spreadsheets
    /// use onto the app's enum. Substring matching rather than exact, because
    /// the portal emits things like "Objected - Reply to Examination Report not
    /// filed" that no exact table would ever cover.
    /// </summary>
    private static MatterStatus? MapStatus(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("registered") || t.Contains("granted")) return MatterStatus.Granted;
        if (t.Contains("abandon") || t.Contains("withdraw") || t.Contains("refus")) return MatterStatus.Abandoned;
        if (t.Contains("expire") || t.Contains("removed") || t.Contains("lapsed")) return MatterStatus.Expired;
        if (t.Contains("active") || t.Contains("valid") || t.Contains("protected")) return MatterStatus.Active;
        if (t.Contains("pending") || t.Contains("objected") || t.Contains("opposed") ||
            t.Contains("examin") || t.Contains("advertis") || t.Contains("accepted") ||
            t.Contains("formalit") || t.Contains("send") || t.Contains("marked")) return MatterStatus.Pending;
        return null;
    }

    /// <summary>
    /// Day-first by default. Indian practice writes 04/12/2019 as 4 December,
    /// and .NET on a machine set to en-US would silently read it as 12 April -
    /// a silent eight-month error on a renewal anchor. ISO is tried first
    /// because it is unambiguous, then explicit day-first formats, and only
    /// then the machine's own culture.
    /// </summary>
    private static DateTime? ParseDate(string? text, int line, string column, List<RowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();

        string[] formats =
        {
            "yyyy-MM-dd", "yyyy/MM/dd",
            "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
            "d/M/yyyy", "d-M-yyyy",
            "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "dd MMMM yyyy"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed.Date;
        }

        if (DateTime.TryParse(value, out var loose))
        {
            issues.Add(new RowIssue(line, column,
                $"'{value}' isn't in an unambiguous format - read as {loose:dd MMM yyyy} using this PC's regional settings. " +
                "Use yyyy-MM-dd to be certain.", false));
            return loose.Date;
        }

        issues.Add(new RowIssue(line, column, $"'{value}' isn't a date I can read - left blank.", false));
        return null;
    }

    private static Matter CloneForUpdate(Matter source) => new()
    {
        Id = source.Id,
        MatterNumber = source.MatterNumber,
        ApplicationNumber = source.ApplicationNumber,
        GrantNumber = source.GrantNumber,
        Title = source.Title,
        ClientName = source.ClientName,
        ProprietorName = source.ProprietorName,
        NiceClass = source.NiceClass,
        MarkType = source.MarkType,
        Status = source.Status,
        Country = source.Country,
        State = source.State,
        AttorneyOfRecord = source.AttorneyOfRecord,
        AttorneyCode = source.AttorneyCode,
        PortalAlert = source.PortalAlert,
        FilingDate = source.FilingDate,
        RegistrationDate = source.RegistrationDate,
        RenewalDueDate = source.RenewalDueDate,
        Type = source.Type,
        AssignedToId = source.AssignedToId
    };

    private static void CopyInto(Matter from, Matter to)
    {
        to.Title = from.Title;
        to.MatterNumber = from.MatterNumber;
        to.ApplicationNumber = from.ApplicationNumber;
        to.GrantNumber = from.GrantNumber;
        to.ClientName = from.ClientName;
        to.ProprietorName = from.ProprietorName;
        to.NiceClass = from.NiceClass;
        to.MarkType = from.MarkType;
        to.Status = from.Status;
        to.Country = from.Country;
        to.State = from.State;
        to.AttorneyOfRecord = from.AttorneyOfRecord;
        to.AttorneyCode = from.AttorneyCode;
        to.PortalAlert = from.PortalAlert;
        to.FilingDate = from.FilingDate;
        to.RegistrationDate = from.RegistrationDate;
        to.RenewalDueDate = from.RenewalDueDate;
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .ToList();

    /// <summary>
    /// Minimal RFC 4180 splitter - handles quoted fields containing commas and
    /// doubled quotes, which a naive Split(',') mangles the moment a proprietor
    /// name contains "Pvt Ltd, Mumbai".
    /// </summary>
    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { cells.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
