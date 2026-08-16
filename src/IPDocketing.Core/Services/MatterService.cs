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

    // --- Search (docx section 6: "Comprehensive search tool") ---

    public List<Matter> SearchByMarkExact(string title) =>
        _db.Matters.Where(m => m.Title == title).ToList();

    public List<Matter> SearchByMarkContains(string fragment) =>
        _db.Matters.Where(m => m.Title.Contains(fragment)).ToList();

    public List<Matter> SearchByMarkStartsWith(string prefix) =>
        _db.Matters.Where(m => m.Title.StartsWith(prefix)).ToList();

    /// <summary>
    /// Simple phonetic match (Soundex-style first-letter + consonant-skeleton
    /// comparison). It's a coarse approximation, not a linguistic phonetic
    /// engine -- good enough to catch near-miss spellings of a mark, not a
    /// substitute for a trademark examiner's confusing-similarity analysis.
    /// </summary>
    public List<Matter> SearchByMarkPhonetic(string mark)
    {
        var key = Soundex(mark);
        return _db.Matters.AsEnumerable()
            .Where(m => Soundex(m.Title) == key)
            .ToList();
    }

    public List<Matter> SearchByProprietor(string proprietorName) =>
        _db.Matters.Where(m => m.ProprietorName != null && m.ProprietorName.Contains(proprietorName)).ToList();

    public List<Matter> SearchByAttorney(string attorneyName) =>
        _db.Matters.Where(m => m.AttorneyOfRecord != null && m.AttorneyOfRecord.Contains(attorneyName)).ToList();

    public List<Matter> SearchByState(string state) =>
        _db.Matters.Where(m => m.State == state).ToList();

    public List<Matter> SearchByAssignee(int teamMemberId) =>
        _db.Matters.Where(m => m.AssignedToId == teamMemberId).ToList();

    private static string Soundex(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.ToUpperInvariant();
        var first = s[0];
        var codes = "01230120022455012623010202";
        var result = new System.Text.StringBuilder().Append(first);
        char lastCode = codes[first - 'A'];
        for (int i = 1; i < s.Length && result.Length < 4; i++)
        {
            if (s[i] < 'A' || s[i] > 'Z') continue;
            var code = codes[s[i] - 'A'];
            if (code != '0' && code != lastCode) result.Append(code);
            lastCode = code;
        }
        return result.ToString().PadRight(4, '0');
    }
}
