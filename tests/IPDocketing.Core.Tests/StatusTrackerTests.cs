using IPDocketing.Core.Models;
using IPDocketing.Core.Services;
using Xunit;

namespace IPDocketing.Core.Tests;

/// <summary>
/// Smoke coverage for the dossier assembly. Its value is less in the assertions
/// than in the fact that it executes every Include and every navigation
/// property against a real schema: a model change that breaks a relationship
/// shows up here as a failing test rather than as a crash on the Status Tracker
/// page.
/// </summary>
public class StatusTrackerTests
{
    private static Matter NewMatter() => new()
    {
        MatterNumber = "TM-0002",
        Title = "AMOR DO VALE",
        ClientName = "Leads Brand Connect",
        Type = MatterType.Trademark,
        Country = "IN",
        ApplicationNumber = "7837113",
        NiceClass = "29"
    };

    [Fact]
    public void AMatterWithNoHistoryStillProducesADossier()
    {
        using var db = new TestDatabase();
        var matter = NewMatter();
        db.Db.Matters.Add(matter);
        db.Db.SaveChanges();

        var dossier = new StatusTrackerService(db.Db).GetDossier(matter.Id);

        Assert.NotNull(dossier);
        Assert.Equal("AMOR DO VALE", dossier!.Matter.Title);
        Assert.Empty(dossier.Events);
        Assert.Empty(dossier.Deadlines);
        Assert.Empty(dossier.Documents);
        Assert.Empty(dossier.Oppositions);
        Assert.Null(dossier.NextDeadline);
        Assert.False(dossier.HasOpenOpposition);
    }

    [Fact]
    public void AMissingMatterReturnsNullRatherThanThrowing()
    {
        using var db = new TestDatabase();
        Assert.Null(new StatusTrackerService(db.Db).GetDossier(9999));
    }

    [Fact]
    public void PrintableOutputsRenderForAnEmptyMatter()
    {
        // The "nothing on file yet" case is the one a newly imported mark is in,
        // and it is exactly the case that never gets exercised by hand.
        using var db = new TestDatabase();
        var matter = NewMatter();
        db.Db.Matters.Add(matter);
        db.Db.SaveChanges();

        var service = new StatusTrackerService(db.Db);
        var dossier = service.GetDossier(matter.Id)!;

        var text = service.BuildPlainText(dossier);
        Assert.Contains("AMOR DO VALE", text);
        Assert.Contains("No events logged.", text);

        var html = service.BuildPrintableHtml(dossier, autoPrint: false);
        Assert.Contains("<html", html);
        Assert.DoesNotContain("window.print()", html);
    }

    [Fact]
    public void MarkTitlesAreHtmlEncodedInThePrintableSheet()
    {
        // A mark containing an ampersand or angle bracket must not be able to
        // break the generated sheet's markup.
        using var db = new TestDatabase();
        var matter = NewMatter();
        matter.Title = "TOM & JERRY <TM>";
        db.Db.Matters.Add(matter);
        db.Db.SaveChanges();

        var service = new StatusTrackerService(db.Db);
        var html = service.BuildPrintableHtml(service.GetDossier(matter.Id)!, autoPrint: false);

        Assert.Contains("TOM &amp; JERRY &lt;TM&gt;", html);
    }
}
