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
        _db.Matters.Include(m => m.ParentMatter).Include(m => m.ChildMatters)
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
}
