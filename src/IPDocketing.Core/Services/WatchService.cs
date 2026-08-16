using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

/// <summary>
/// docx section 4 — Trademark Watch. Runs a local similarity match between
/// marks published in a journal issue (entered manually today, since no
/// live IP-India feed is connected yet — see IIndiaIpSearchConnector) and
/// your own portfolio, and records the ones worth a human look.
/// </summary>
public class WatchService
{
    private readonly AppDbContext _db;
    private const int AlertThreshold = 60;

    public WatchService(AppDbContext db)
    {
        _db = db;
    }

    public List<WatchAlert> GetAll() =>
        _db.WatchAlerts.Include(w => w.Matter).Include(w => w.JournalIssue)
            .Where(w => !w.Dismissed)
            .OrderByDescending(w => w.SimilarityScore)
            .ToList();

    /// <summary>
    /// Compares each published mark against every portfolio matter title and
    /// stores a WatchAlert for any pairing scoring at or above the
    /// threshold. Returns the alerts created in this run.
    /// </summary>
    public List<WatchAlert> RunWatch(int journalIssueId, IEnumerable<(string Mark, string? Applicant)> publishedMarks)
    {
        var portfolio = _db.Matters.Where(m => m.Type == MatterType.Trademark).ToList();
        var created = new List<WatchAlert>();

        foreach (var (mark, applicant) in publishedMarks)
        {
            if (string.IsNullOrWhiteSpace(mark)) continue;

            foreach (var matter in portfolio)
            {
                var score = SimilarityScore(mark, matter.Title);
                if (score < AlertThreshold) continue;

                var alert = new WatchAlert
                {
                    JournalIssueId = journalIssueId,
                    PublishedMark = mark,
                    PublishedApplicant = applicant,
                    MatterId = matter.Id,
                    SimilarityScore = score
                };
                _db.WatchAlerts.Add(alert);
                created.Add(alert);
            }
        }

        if (created.Count > 0) _db.SaveChanges();
        return created;
    }

    public void Dismiss(int alertId)
    {
        var alert = _db.WatchAlerts.Find(alertId);
        if (alert is null) return;
        alert.Dismissed = true;
        _db.SaveChanges();
    }

    /// <summary>
    /// Normalized Levenshtein similarity (0-100). A plain edit-distance
    /// score, not a trademark "likelihood of confusion" test -- treat
    /// results as a shortlist to review, not a legal conclusion.
    /// </summary>
    private static int SimilarityScore(string a, string b)
    {
        a = a.Trim().ToUpperInvariant();
        b = b.Trim().ToUpperInvariant();
        if (a.Length == 0 || b.Length == 0) return 0;

        var distance = Levenshtein(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return (int)Math.Round((1.0 - (double)distance / maxLen) * 100);
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }
}
