using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

public class MatterService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public MatterService(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<Matter> GetAll() =>
        _db.Matters.Include(m => m.ParentMatter).Include(m => m.ChildMatters).Include(m => m.AssignedTo)
            .OrderBy(m => m.MatterNumber).ToList();

    public Matter? GetById(int id) =>
        _db.Matters
            .Include(m => m.Events)
            .Include(m => m.Deadlines)
            .Include(m => m.Documents)
            .Include(m => m.ChildMatters)
            .Include(m => m.ParentMatter)
            .FirstOrDefault(m => m.Id == id);

    public Matter Add(Matter matter)
    {
        matter.CreatedDate = DateTime.UtcNow;
        _db.Matters.Add(matter);
        _db.SaveChanges();
        _audit.Log("Create", "Matter", matter.Id, $"Matter {matter.MatterNumber} - {matter.Title} created.");
        return matter;
    }

    public void Update(Matter matter)
    {
        _db.Matters.Update(matter);
        _db.SaveChanges();
        _audit.Log("Update", "Matter", matter.Id, $"Matter {matter.MatterNumber} updated.");
    }

    public void Delete(int id)
    {
        var matter = _db.Matters.Find(id);
        if (matter is null) return;
        _db.Matters.Remove(matter);
        _db.SaveChanges();
        _audit.Log("Delete", "Matter", id, $"Matter {matter.MatterNumber} deleted.");
    }

    /// <summary>Family tree: root ancestor plus all descendants (continuations, foreign equivalents).</summary>
    public List<Matter> GetFamily(int matterId)
    {
        var matter = GetById(matterId);
        if (matter is null) return new List<Matter>();

        var root = matter;
        while (root.ParentMatterId is not null)
        {
            var p = GetById(root.ParentMatterId.Value);
            if (p is null) break;
            root = p;
        }

        var family = new List<Matter> { root };
        CollectDescendants(root, family);
        return family;
    }

    private void CollectDescendants(Matter node, List<Matter> acc)
    {
        foreach (var child in _db.Matters.Where(m => m.ParentMatterId == node.Id).ToList())
        {
            acc.Add(child);
            CollectDescendants(child, acc);
        }
    }

    // --- Search (docx section 6: "Comprehensive trademark search") ---
    //
    // Phase 30: the four mark-matching modes and the three "additional
    // search" axes from the spec are now one composable query object rather
    // than seven unrelated methods, because the spec asks for the result set
    // to *then* be filtered by status and by portal alert - which the old
    // one-method-per-axis shape could not express. The individual methods are
    // kept below so nothing that already called them breaks.

    public enum MarkMatchMode
    {
        Contains,
        Exact,
        StartsWith,
        Phonetic
    }

    /// <summary>
    /// One comprehensive search. Every field is optional; empty fields are
    /// simply not applied, so this doubles as "list everything".
    /// </summary>
    public sealed class MarkSearchQuery
    {
        public string? Mark { get; set; }
        public MarkMatchMode Mode { get; set; } = MarkMatchMode.Contains;

        public string? Proprietor { get; set; }
        public string? Attorney { get; set; }
        public string? State { get; set; }
        public string? NiceClass { get; set; }

        /// <summary>Word vs device mark - docx splits the search by this.</summary>
        public MarkType? MarkType { get; set; }

        /// <summary>Filter on status of the mark (docx section 6, filter a).</summary>
        public MatterStatus? Status { get; set; }

        /// <summary>Filter on the alert text captured from the TMR status page (docx section 6, filter b).</summary>
        public string? Alert { get; set; }

        /// <summary>When true, only marks that carry SOME alert are returned.</summary>
        public bool OnlyWithAlerts { get; set; }

        /// <summary>Restrict to trademarks only, ignoring patents/copyright/trade secrets.</summary>
        public bool TrademarksOnly { get; set; } = true;
    }

    public List<Matter> Search(MarkSearchQuery query)
    {
        // Materialised first: phonetic matching and case-insensitive Contains
        // can't be translated to SQLite by EF, and the local portfolio is a
        // few thousand rows at most, so in-memory filtering is both correct
        // and fast enough. Doing it the other way round silently drops the
        // phonetic mode or throws at query-translation time.
        IEnumerable<Matter> results = _db.Matters
            .Include(m => m.AssignedTo)
            .AsEnumerable();

        if (query.TrademarksOnly)
            results = results.Where(m => m.Type == MatterType.Trademark);

        var mark = query.Mark?.Trim();
        if (!string.IsNullOrEmpty(mark))
        {
            results = query.Mode switch
            {
                MarkMatchMode.Exact => results.Where(m =>
                    string.Equals(m.Title, mark, StringComparison.OrdinalIgnoreCase)),
                MarkMatchMode.StartsWith => results.Where(m =>
                    m.Title.StartsWith(mark, StringComparison.OrdinalIgnoreCase)),
                MarkMatchMode.Phonetic => PhoneticFilter(results, mark),
                _ => results.Where(m => m.Title.Contains(mark, StringComparison.OrdinalIgnoreCase)),
            };
        }

        results = ApplyContains(results, query.Proprietor, m => m.ProprietorName);
        results = ApplyContains(results, query.Attorney, m => m.AttorneyOfRecord);
        results = ApplyContains(results, query.State, m => m.State);
        results = ApplyContains(results, query.NiceClass, m => m.NiceClass);
        results = ApplyContains(results, query.Alert, m => m.PortalAlert);

        if (query.MarkType is not null)
            results = results.Where(m => m.MarkType == query.MarkType);

        if (query.Status is not null)
            results = results.Where(m => m.Status == query.Status);

        if (query.OnlyWithAlerts)
            results = results.Where(m => !string.IsNullOrWhiteSpace(m.PortalAlert));

        return results.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<Matter> ApplyContains(
        IEnumerable<Matter> source, string? term, Func<Matter, string?> selector)
    {
        var needle = term?.Trim();
        if (string.IsNullOrEmpty(needle)) return source;
        return source.Where(m =>
        {
            var value = selector(m);
            return value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Phonetic bucket match. Compares the Soundex key of the whole mark AND of
    /// its first word, so "KWIK BRITE" still matches "QUICK BRIGHT" - single-key
    /// matching on the full string misses multi-word marks almost entirely.
    /// </summary>
    private static IEnumerable<Matter> PhoneticFilter(IEnumerable<Matter> source, string mark)
    {
        var full = Soundex(mark);
        var firstWord = Soundex(FirstWord(mark));

        return source.Where(m =>
            (full.Length > 0 && Soundex(m.Title) == full) ||
            (firstWord.Length > 0 && Soundex(FirstWord(m.Title)) == firstWord));
    }

    private static string FirstWord(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    // --- Individual search axes, kept for existing callers ---

    public List<Matter> SearchByMarkExact(string title) =>
        Search(new MarkSearchQuery { Mark = title, Mode = MarkMatchMode.Exact, TrademarksOnly = false });

    public List<Matter> SearchByMarkContains(string fragment) =>
        Search(new MarkSearchQuery { Mark = fragment, Mode = MarkMatchMode.Contains, TrademarksOnly = false });

    public List<Matter> SearchByMarkStartsWith(string prefix) =>
        Search(new MarkSearchQuery { Mark = prefix, Mode = MarkMatchMode.StartsWith, TrademarksOnly = false });

    /// <summary>
    /// Coarse phonetic approximation, not a linguistic phonetic engine - good
    /// enough to surface near-miss spellings of a mark, and no substitute for
    /// an examiner's confusing-similarity analysis.
    /// </summary>
    public List<Matter> SearchByMarkPhonetic(string mark) =>
        Search(new MarkSearchQuery { Mark = mark, Mode = MarkMatchMode.Phonetic, TrademarksOnly = false });

    public List<Matter> SearchByProprietor(string proprietorName) =>
        Search(new MarkSearchQuery { Proprietor = proprietorName, TrademarksOnly = false });

    public List<Matter> SearchByAttorney(string attorneyName) =>
        Search(new MarkSearchQuery { Attorney = attorneyName, TrademarksOnly = false });

    public List<Matter> SearchByState(string state) =>
        Search(new MarkSearchQuery { State = state, TrademarksOnly = false });

    public List<Matter> SearchByAssignee(int teamMemberId) =>
        _db.Matters.Where(m => m.AssignedToId == teamMemberId).ToList();

    /// <summary>Distinct alert strings currently in the portfolio, for the search page's filter list.</summary>
    public List<string> GetKnownAlerts() =>
        _db.Matters
            .Select(m => m.PortalAlert)
            .Where(a => a != null && a != "")
            .Distinct()
            .OrderBy(a => a)
            .ToList()!;

    /// <summary>
    /// Soundex key. Phase 30 fix: the previous implementation indexed
    /// codes[first - 'A'] straight off the first character, which threw
    /// IndexOutOfRangeException on any mark starting with a digit, an ampersand
    /// or a non-Latin letter - and Indian marks routinely start with digits
    /// ("5 STAR") or Devanagari. Non-letters are now stripped up front and an
    /// empty key is returned rather than crashing the search.
    /// </summary>
    private static string Soundex(string? s)
    {
        // Phase 35: delegate to the shared phonetic key, which folds the
        // Indian-English spelling variants that actually generate near-miss
        // pairs on this register (KSH/X, PH/F, V/W, doubled letters,
        // transliterated vowels). Classic Soundex treats LAXMI and LAKSHMI as
        // unrelated, which on an Indian portfolio is the single most common
        // variant there is.
        if (!string.IsNullOrWhiteSpace(s))
        {
            var key = MarkSimilarityService.PhoneticKey(s);
            if (key.Length > 0) return key;
        }

        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var letters = new System.Text.StringBuilder();
        foreach (var ch in s.ToUpperInvariant())
            if (ch is >= 'A' and <= 'Z') letters.Append(ch);

        if (letters.Length == 0) return string.Empty;

        const string codes = "01230120022455012623010202";
        var normalized = letters.ToString();
        var result = new System.Text.StringBuilder().Append(normalized[0]);
        var lastCode = codes[normalized[0] - 'A'];

        for (var i = 1; i < normalized.Length && result.Length < 4; i++)
        {
            var code = codes[normalized[i] - 'A'];
            if (code != '0' && code != lastCode) result.Append(code);
            lastCode = code;
        }

        return result.ToString().PadRight(4, '0');
    }
}
