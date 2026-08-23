using IPDocketing.Core.Services;
using Xunit;

namespace IPDocketing.Core.Tests;

/// <summary>
/// Journal listing labels come in three shapes across the years the Registry
/// has published them, and the class-range lookup silently found nothing on the
/// shapes it did not handle.
/// </summary>
public class ClassRangeTests
{
    [Theory]
    [InlineData("CLASS 26 - 34", 26, 34)]   // current listing
    [InlineData("CLASS_26_-_34", 26, 34)]   // ~2012 and earlier, underscores
    [InlineData("CLASS_1-4", 1, 4)]         // no spaces at all
    [InlineData("class 1 - 9", 1, 9)]       // case must not matter
    [InlineData("CLASS 26 – 34", 26, 34)] // en dash, not hyphen
    public void ParsesEveryPublishedLabelShape(string label, int expectedLow, int expectedHigh)
    {
        Assert.True(JournalFetchService.TryParseClassRange(label, out var low, out var high));
        Assert.Equal(expectedLow, low);
        Assert.Equal(expectedHigh, high);
    }

    [Theory]
    [InlineData("NOTICE")]
    [InlineData("WELL KNOWN TRADE MARKS")]
    [InlineData("CLASS_35_PART_1")]   // a single class split into parts, not a range
    [InlineData("Download 1")]        // icon link with a derived name
    [InlineData("")]
    public void RejectsLabelsThatCarryNoRange(string label)
    {
        // These are real links, just not class-range ones. Returning false is
        // correct; the caller reports "this issue's links don't say which
        // classes they cover" rather than pretending one of them matches.
        Assert.False(JournalFetchService.TryParseClassRange(label, out _, out _));
    }

    [Fact]
    public void RejectsAnInvertedRange()
    {
        // 34-26 is not a range, it is a parse gone wrong. Accepting it would
        // make every class between 26 and 34 match nothing.
        Assert.False(JournalFetchService.TryParseClassRange("CLASS 34 - 26", out _, out _));
    }
}
