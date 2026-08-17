using System.Text;

namespace IPDocketing.Core.Services;

/// <summary>
/// Turns a table lifted off a web page into CSV that
/// <see cref="PortfolioImportService"/> can read.
///
/// The point of this indirection: the app has exactly one import path, with one
/// set of validation rules, one date parser and one preview step. A portfolio
/// arriving from your CEFS dashboard goes through precisely the same checks as
/// one arriving from a spreadsheet - no second, weaker code path that skips the
/// day-first date handling or the duplicate detection.
///
/// Column mapping is a guess that the user confirms, never a guess the app acts
/// on silently. <see cref="GuessMapping"/> proposes; the UI shows the proposal;
/// nothing is imported until it is accepted. Getting a column wrong here would
/// write hundreds of records with, say, a registration date in the filing date
/// field - which then silently anchors every renewal term eight months to
/// several years out.
/// </summary>
public class TableImportMapper
{
    /// <summary>The importer target for a source column. Empty means "ignore this column".</summary>
    public const string Ignore = "(ignore)";

    /// <summary>
    /// Target columns offered in the mapping UI, in a sensible reading order.
    /// These are exactly the names <see cref="PortfolioImportService"/> accepts.
    /// </summary>
    public static readonly IReadOnlyList<string> Targets = new[]
    {
        Ignore,
        "Mark",
        "ApplicationNumber",
        "RegistrationNumber",
        "MatterNumber",
        "Class",
        "Status",
        "Proprietor",
        "Client",
        "MarkType",
        "AttorneyOfRecord",
        "AttorneyCode",
        "State",
        "Country",
        "FilingDate",
        "RegistrationDate",
        "RenewalDueDate",
        "PortalAlert",
    };

    /// <summary>
    /// Header wordings seen on Indian IP portals and exports, mapped to targets.
    /// Matched as substrings, longest first, because portal headers are verbose
    /// ("Date of Application (DD/MM/YYYY)") and rarely match anything exactly.
    /// </summary>
    private static readonly (string Needle, string Target)[] Hints =
    {
        ("date of application", "FilingDate"),
        ("application date", "FilingDate"),
        ("filing date", "FilingDate"),
        ("date of filing", "FilingDate"),

        ("date of registration", "RegistrationDate"),
        ("registration date", "RegistrationDate"),
        ("registered on", "RegistrationDate"),

        ("valid upto", "RenewalDueDate"),
        ("valid up to", "RenewalDueDate"),
        ("renewal date", "RenewalDueDate"),
        ("renewal due", "RenewalDueDate"),
        ("expiry", "RenewalDueDate"),

        ("registration no", "RegistrationNumber"),
        ("registration number", "RegistrationNumber"),
        ("regn no", "RegistrationNumber"),

        ("application no", "ApplicationNumber"),
        ("application number", "ApplicationNumber"),
        ("app no", "ApplicationNumber"),
        ("tm application", "ApplicationNumber"),
        ("trade mark no", "ApplicationNumber"),
        ("trademark no", "ApplicationNumber"),
        ("diary no", "ApplicationNumber"),

        ("word mark", "Mark"),
        ("wordmark", "Mark"),
        ("trade mark", "Mark"),
        ("trademark", "Mark"),
        ("mark name", "Mark"),
        ("brand", "Mark"),
        ("title", "Mark"),

        ("proprietor", "Proprietor"),
        ("applicant name", "Proprietor"),
        ("applicant", "Proprietor"),
        ("owner", "Proprietor"),
        ("holder", "Proprietor"),

        ("client", "Client"),

        ("attorney code", "AttorneyCode"),
        ("agent code", "AttorneyCode"),
        ("agent registration", "AttorneyCode"),
        ("attorney", "AttorneyOfRecord"),
        ("agent", "AttorneyOfRecord"),

        ("our ref", "MatterNumber"),
        ("your ref", "MatterNumber"),
        ("reference", "MatterNumber"),
        ("file no", "MatterNumber"),

        ("class", "Class"),
        ("status", "Status"),
        ("remark", "PortalAlert"),
        ("state", "State"),
        ("country", "Country"),
        ("type of mark", "MarkType"),
        ("mark type", "MarkType"),
    };

    /// <summary>
    /// Proposes a target for each source header. Each target is used at most
    /// once - if two columns both look like "Application No", only the first
    /// wins and the second is left for the user to resolve, rather than the
    /// later one silently overwriting the earlier.
    /// </summary>
    public List<string> GuessMapping(IReadOnlyList<string> headers)
    {
        var mapping = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var normalized = (header ?? string.Empty)
                .ToLowerInvariant()
                .Replace("_", " ")
                .Replace(".", " ");

            var target = Ignore;

            foreach (var (needle, candidate) in Hints.OrderByDescending(h => h.Needle.Length))
            {
                if (!normalized.Contains(needle)) continue;
                if (used.Contains(candidate)) continue;
                target = candidate;
                break;
            }

            if (target != Ignore) used.Add(target);
            mapping.Add(target);
        }

        return mapping;
    }

    /// <summary>
    /// Builds importer-shaped CSV from the table and the confirmed mapping.
    /// Rows where every mapped cell is blank are dropped - portal grids
    /// routinely carry a trailing "no records" or pagination row.
    /// </summary>
    public string BuildCsv(IReadOnlyList<string> mapping, IEnumerable<IReadOnlyList<string>> rows)
    {
        var columns = mapping
            .Select((target, index) => (Target: target, Index: index))
            .Where(c => c.Target != Ignore)
            .ToList();

        if (columns.Count == 0)
            throw new InvalidOperationException("No columns were mapped, so there is nothing to import.");

        if (!columns.Any(c => c.Target == "Mark"))
            throw new InvalidOperationException(
                "No column is mapped to Mark. The importer needs the mark itself to create a record.");

        var sb = new StringBuilder(string.Join(',', columns.Select(c => c.Target))).Append('\n');
        var written = 0;

        foreach (var row in rows)
        {
            var cells = columns
                .Select(c => c.Index < row.Count ? (row[c.Index] ?? string.Empty).Trim() : string.Empty)
                .ToList();

            if (cells.All(string.IsNullOrWhiteSpace)) continue;

            sb.AppendLine(string.Join(',', cells.Select(Csv)));
            written++;
        }

        if (written == 0)
            throw new InvalidOperationException("Every row was blank once the mapping was applied.");

        return sb.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
