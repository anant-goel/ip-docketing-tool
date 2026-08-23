using IPDocketing.Core.Models;
using IPDocketing.Core.Services;
using Xunit;

namespace IPDocketing.Core.Tests;

/// <summary>
/// These run against a real SQLite file on purpose — see TestDatabase for why.
/// </summary>
public class WatchServiceTests
{
    private static (Matter matter, JournalIssue issue) Seed(TestDatabase db, string portfolioMark)
    {
        var matter = new Matter
        {
            MatterNumber = "TM-0001",
            Title = portfolioMark,
            ClientName = "Test Client",
            Type = MatterType.Trademark,
            Country = "IN"
        };

        var issue = new JournalIssue
        {
            IssueNumber = "2273",
            PublicationDate = new DateTime(2026, 8, 10)
        };

        db.Db.Matters.Add(matter);
        db.Db.JournalIssues.Add(issue);
        db.Db.SaveChanges();

        return (matter, issue);
    }

    [Fact]
    public void RunWatchExecutesAgainstARealDatabase()
    {
        // THE REGRESSION THIS EXISTS FOR.
        //
        // The dedupe key used to be built inside the LINQ query as
        // `w.PublishedMark + "|" + w.MatterId`. C# compiles string + int to
        // String.Concat(object, object), which EF Core cannot translate to SQL,
        // so RunWatch threw "The LINQ expression could not be translated" on
        // every single invocation. The watch did nothing, and nothing said why.
        //
        // Any query EF cannot translate will fail this test at the call.
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");
        var watch = new WatchService(db.Db);

        var created = watch.RunWatch(issue.Id, new[]
        {
            ("SHUBH LAXMI FOODS PVT LTD", (string?)"Someone Else"),
        });

        Assert.NotEmpty(created);
    }

    [Fact]
    public void AlertsArePersistedNotJustReturned()
    {
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");
        new WatchService(db.Db).RunWatch(issue.Id, new[]
        {
            ("SHUBH LAXMI FOODS PVT LTD", (string?)null),
        });

        // Read back through a second context: proves it reached the file, not
        // merely the change tracker.
        using var reopened = db.Reopen();
        Assert.NotEmpty(reopened.WatchAlerts.ToList());
    }

    [Fact]
    public void ReRunningTheSameIssueDoesNotDuplicateAlerts()
    {
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");
        var watch = new WatchService(db.Db);

        var marks = new[] { ("SHUBH LAXMI FOODS PVT LTD", (string?)null) };

        watch.RunWatch(issue.Id, marks);
        var countAfterFirst = db.Db.WatchAlerts.Count();

        var secondPass = watch.RunWatch(issue.Id, marks);

        Assert.Empty(secondPass);
        Assert.Equal(countAfterFirst, db.Db.WatchAlerts.Count());
    }

    [Fact]
    public void CasingAndStrayWhitespaceDoNotCreateASecondAlert()
    {
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");
        var watch = new WatchService(db.Db);

        watch.RunWatch(issue.Id, new[] { ("SHUBH LAXMI FOODS PVT LTD", (string?)null) });
        var afterFirst = db.Db.WatchAlerts.Count();

        // Same published mark, different transcription. OCR and manual paste
        // both produce this, and it used to raise a second alert for the same
        // pairing.
        watch.RunWatch(issue.Id, new[] { ("  Shubh Laxmi Foods Pvt Ltd  ", (string?)null) });

        Assert.Equal(afterFirst, db.Db.WatchAlerts.Count());
    }

    [Fact]
    public void AnUnrelatedMarkRaisesNothing()
    {
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");

        var created = new WatchService(db.Db).RunWatch(issue.Id, new[]
        {
            ("ZEBRA CROSSING TELECOM", (string?)null),
        });

        Assert.Empty(created);
    }

    [Fact]
    public void EveryAlertRecordsWhyItFired()
    {
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");

        var created = new WatchService(db.Db).RunWatch(issue.Id, new[]
        {
            ("SHUBH LAXMI FOODS PVT LTD", (string?)null),
        });

        var alert = Assert.Single(created);
        Assert.False(string.IsNullOrWhiteSpace(alert.PrimarySignal));
        Assert.False(string.IsNullOrWhiteSpace(alert.MatchExplanation));
    }

    [Fact]
    public void AnEmptyPublishedMarkIsSkippedRatherThanMatched()
    {
        using var db = new TestDatabase();
        var (_, issue) = Seed(db, "SHUBH LAXMI");

        var created = new WatchService(db.Db).RunWatch(issue.Id, new[]
        {
            ("", (string?)null),
            ("   ", (string?)null),
        });

        Assert.Empty(created);
    }

    [Fact]
    public void ReportBuildersRunWithNoAlertsOnFile()
    {
        // An empty portfolio used to be the case nobody exercised, and a report
        // that throws on "nothing found" is a report you cannot trust on a quiet
        // week.
        using var db = new TestDatabase();
        var watch = new WatchService(db.Db);

        Assert.Contains("<html", watch.BuildWatchReportHtml(autoPrint: false));
        Assert.Contains("Client", watch.BuildWatchReportCsv());
    }
}
