using IPDocketing.Core.Models;
using IPDocketing.Core.Services;
using Xunit;

namespace IPDocketing.Core.Tests;

public class JournalServiceTests
{
    private static JournalIssue Issue(string number, string? url = null, string? pdf = null) => new()
    {
        IssueNumber = number,
        PublicationDate = new DateTime(2026, 8, 10),
        Url = url ?? string.Empty,
        LocalPdfPath = pdf
    };

    [Fact]
    public void AddingTheSameIssueTwiceKeepsOneRow()
    {
        // Several paths add issues - auto-fetch, the weekly pull, browse and
        // download, the background sync - and none of them used to check
        // whether the issue was already on file. Journal 2274 appeared twice,
        // was searched twice, and reported twice.
        using var db = new TestDatabase();
        var journal = new JournalService(db.Db);

        journal.Add(Issue("2274"));
        journal.Add(Issue("2274"));

        Assert.Single(journal.GetAll());
    }

    [Fact]
    public void ASecondAddEnrichesTheExistingRowRatherThanReplacingIt()
    {
        using var db = new TestDatabase();
        var journal = new JournalService(db.Db);

        journal.Add(Issue("2274", pdf: @"C:\library\journal_2274.pdf"));
        journal.Add(Issue("2274", url: "https://example.invalid/2274.pdf"));

        var stored = Assert.Single(journal.GetAll());

        // The URL arrived on the second pass and must be recorded...
        Assert.Equal("https://example.invalid/2274.pdf", stored.Url);
        // ...without discarding the downloaded file the first pass found.
        Assert.Equal(@"C:\library\journal_2274.pdf", stored.LocalPdfPath);
    }

    [Fact]
    public void RemoveDuplicatesKeepsTheCopyThatHasThePdf()
    {
        using var db = new TestDatabase();

        // Inserted straight through the context to simulate rows created before
        // deduplication existed.
        db.Db.JournalIssues.Add(Issue("2274"));
        db.Db.JournalIssues.Add(Issue("2274", pdf: @"C:\library\journal_2274.pdf"));
        db.Db.JournalIssues.Add(Issue("2274"));
        db.Db.SaveChanges();

        var journal = new JournalService(db.Db);
        var removed = journal.RemoveDuplicates();

        Assert.Equal(2, removed);
        var survivor = Assert.Single(journal.GetAll());
        Assert.Equal(@"C:\library\journal_2274.pdf", survivor.LocalPdfPath);
    }

    [Fact]
    public void MarkReviewedTogglesAndPersists()
    {
        using var db = new TestDatabase();
        var journal = new JournalService(db.Db);
        var issue = journal.Add(Issue("2274"));

        journal.MarkReviewed(issue.Id);

        using var reopened = db.Reopen();
        Assert.True(reopened.JournalIssues.Single().Reviewed);
    }

    [Fact]
    public void MarkReviewedOnAMissingIdIsIgnoredRatherThanThrowing()
    {
        using var db = new TestDatabase();
        var journal = new JournalService(db.Db);

        var exception = Record.Exception(() => journal.MarkReviewed(9999));

        Assert.Null(exception);
    }

    [Fact]
    public void IssuesComeBackNewestFirst()
    {
        using var db = new TestDatabase();
        var journal = new JournalService(db.Db);

        journal.Add(new JournalIssue { IssueNumber = "2271", PublicationDate = new DateTime(2026, 7, 27) });
        journal.Add(new JournalIssue { IssueNumber = "2274", PublicationDate = new DateTime(2026, 8, 17) });
        journal.Add(new JournalIssue { IssueNumber = "2272", PublicationDate = new DateTime(2026, 8, 3) });

        Assert.Equal(new[] { "2274", "2272", "2271" }, journal.GetAll().Select(j => j.IssueNumber));
    }
}
