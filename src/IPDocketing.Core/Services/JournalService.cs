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
        // Deduplicate by issue number. Your screenshot showed Journal 2274
        // listed twice: several code paths add issues (auto-fetch, the weekly
        // pull, browse-and-download) and none of them checked whether the issue
        // was already on file, so every pass appended another copy. Duplicates
        // then get searched and reported twice, which makes the whole list look
        // untrustworthy.
        //
        // An existing row is updated rather than replaced, so a local PDF path
        // recorded by one path is never wiped out by another.
        var existing = _db.JournalIssues
            .FirstOrDefault(j => j.IssueNumber == issue.IssueNumber);

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(issue.Url)) existing.Url = issue.Url;
            if (!string.IsNullOrWhiteSpace(issue.LocalPdfPath))
            {
                existing.LocalPdfPath = issue.LocalPdfPath;
                existing.PdfSizeBytes = issue.PdfSizeBytes;
                existing.DownloadedUtc = issue.DownloadedUtc;
            }
            if (!string.IsNullOrWhiteSpace(issue.Notes)) existing.Notes = issue.Notes;
            if (issue.PublicationDate != default) existing.PublicationDate = issue.PublicationDate;

            _db.SaveChanges();
            return existing;
        }

        _db.JournalIssues.Add(issue);
        _db.SaveChanges();
        return issue;
    }

    /// <summary>
    /// Removes duplicate issue rows created before deduplication existed,
    /// keeping whichever copy has a downloaded PDF.
    /// </summary>
    public int RemoveDuplicates()
    {
        var groups = _db.JournalIssues
            .ToList()
            .GroupBy(j => j.IssueNumber)
            .Where(g => g.Count() > 1)
            .ToList();

        var removed = 0;
        foreach (var group in groups)
        {
            var keep = group
                .OrderByDescending(j => !string.IsNullOrWhiteSpace(j.LocalPdfPath))
                .ThenByDescending(j => j.Id)
                .First();

            foreach (var duplicate in group.Where(j => j.Id != keep.Id))
            {
                _db.JournalIssues.Remove(duplicate);
                removed++;
            }
        }

        if (removed > 0) _db.SaveChanges();
        return removed;
    }

    public void MarkReviewed(int id, bool reviewed = true)
    {
        var issue = _db.JournalIssues.Find(id);
        if (issue is null) return;
        issue.Reviewed = reviewed;
        _db.SaveChanges();
    }
}
