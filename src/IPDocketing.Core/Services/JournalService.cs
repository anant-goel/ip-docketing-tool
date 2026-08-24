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

            // `!= default` is not the guard this needs. default(DateTime) is
            // 0001-01-01, but the sentinel this application actually produces is
            // 1601-01-01 - the WinRT epoch an unset date picker returns - and
            // that sailed straight through. One browse-and-download pass with no
            // date chosen then overwrote a correctly parsed publication date,
            // permanently: the row sorted to the bottom of every list and
            // dropped out of the search window, so the newest issue became the
            // one that was never searched.
            if (IsRealDate(issue.PublicationDate)) existing.PublicationDate = issue.PublicationDate;

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
            // KEEP THE ROW THAT CAN STILL DO SOMETHING.
            //
            // The old ranking asked only whether LocalPdfPath was non-empty -
            // not whether the file still existed, and it ignored Url entirely.
            // So a row whose PDF the user had since deleted beat a row holding
            // the only download link, the link row was deleted, and the issue
            // became permanently un-fetchable: the search then reported "no PDF
            // link on record" about an issue whose link this method had just
            // thrown away.
            //
            // Order now: a PDF that is actually on disk, then a usable URL, then
            // a recorded path, then the newest row.
            var keep = group
                .OrderByDescending(j => !string.IsNullOrWhiteSpace(j.LocalPdfPath) && File.Exists(j.LocalPdfPath))
                .ThenByDescending(j => !string.IsNullOrWhiteSpace(j.Url))
                .ThenByDescending(j => !string.IsNullOrWhiteSpace(j.LocalPdfPath))
                .ThenByDescending(j => j.Id)
                .First();

            // Anything the survivor is missing and a doomed row still has is
            // carried across before that row is deleted - otherwise merging two
            // half-complete rows loses whichever half the loser held.
            foreach (var other in group.Where(j => j.Id != keep.Id))
            {
                if (string.IsNullOrWhiteSpace(keep.Url) && !string.IsNullOrWhiteSpace(other.Url))
                    keep.Url = other.Url;

                if ((string.IsNullOrWhiteSpace(keep.LocalPdfPath) || !File.Exists(keep.LocalPdfPath)) &&
                    !string.IsNullOrWhiteSpace(other.LocalPdfPath) && File.Exists(other.LocalPdfPath))
                {
                    keep.LocalPdfPath = other.LocalPdfPath;
                    keep.PdfSizeBytes = other.PdfSizeBytes;
                    keep.DownloadedUtc = other.DownloadedUtc;
                }

                if (!IsRealDate(keep.PublicationDate) && IsRealDate(other.PublicationDate))
                    keep.PublicationDate = other.PublicationDate;
            }

            foreach (var duplicate in group.Where(j => j.Id != keep.Id))
            {
                _db.JournalIssues.Remove(duplicate);
                removed++;
            }
        }

        if (removed > 0) _db.SaveChanges();
        return removed;
    }

    /// <summary>
    /// A publication date that came from a person or a page, rather than from an
    /// unset control. Covers both sentinels: DateTime.MinValue (0001) and the
    /// WinRT epoch (1601) that a blank WinUI date picker hands back.
    /// </summary>
    private static bool IsRealDate(DateTime value) => value.Year >= 1900;

    public void MarkReviewed(int id, bool reviewed = true)
    {
        var issue = _db.JournalIssues.Find(id);
        if (issue is null) return;
        issue.Reviewed = reviewed;
        _db.SaveChanges();
    }
}
