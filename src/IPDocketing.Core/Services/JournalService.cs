using IPDocketing.Core.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.Core.Services;

public class JournalService
{
    private readonly AppDbContext _db;

    public JournalService(AppDbContext db)
    {
        _db = db;
    }

    public List<JournalIssue> GetAll() =>
        _db.JournalIssues.OrderByDescending(j => j.PublicationDate).ToList();

    public JournalIssue Add(JournalIssue issue)
    {
        _db.JournalIssues.Add(issue);
        _db.SaveChanges();
        return issue;
    }

    public void MarkReviewed(int id, bool reviewed = true)
    {
        var issue = _db.JournalIssues.Find(id);
        if (issue is null) return;
        issue.Reviewed = reviewed;
        _db.SaveChanges();
    }
}
