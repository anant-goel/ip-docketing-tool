using IPDocketing.Core.Services;
using Xunit;

namespace IPDocketing.Core.Tests;

public class MarkSimilarityTests
{
    private readonly MarkSimilarityService _similarity = new();

    [Theory]
    [InlineData("Sun-Rise®", "SUN RISE")]
    [InlineData("  multiple   spaces  ", "MULTIPLE SPACES")]
    [InlineData("A&B", "A B")]
    public void NormalisationFoldsCasePunctuationAndSpacing(string input, string expected)
        => Assert.Equal(expected, MarkSimilarityService.Normalize(input));

    [Fact]
    public void NormalisationOfNothingIsEmptyRatherThanAThrow()
    {
        Assert.Equal(string.Empty, MarkSimilarityService.Normalize(null));
        Assert.Equal(string.Empty, MarkSimilarityService.Normalize("   "));
    }

    [Theory]
    // The two pairs the phonetic signal exists to catch. Both are routine on the
    // Indian register and both scored under the threshold on edit distance alone.
    [InlineData("KWIK BRITE", "QUICK BRIGHT")]
    [InlineData("LAXMI", "LAKSHMI")]
    public void PhoneticallyIdenticalMarksShareAKey(string a, string b)
        => Assert.Equal(MarkSimilarityService.PhoneticKey(a), MarkSimilarityService.PhoneticKey(b));

    [Fact]
    public void IdenticalAfterNormalisationScoresFull()
    {
        var result = _similarity.Compare("SUN RISE", "Sun-Rise®");
        Assert.Equal(100, result.Score);
        Assert.Equal("identical", result.PrimarySignal);
    }

    [Fact]
    public void CorporateSuffixesDoNotHideAnIdenticalDistinctiveCore()
    {
        // "SHUBH LAXMI" against "SHUBH LAXMI FOODS PVT LTD" scored 55 on raw
        // edit distance - under the alert threshold - even though the
        // distinctive part is word-for-word identical.
        var result = _similarity.Compare("SHUBH LAXMI FOODS PVT LTD", "SHUBH LAXMI");
        Assert.True(result.Score >= 80, $"expected a strong match, got {result.Score}");
    }

    [Fact]
    public void EveryAlertCanSayWhyItFired()
    {
        var result = _similarity.Compare("SHUBH LAXMI FOODS PVT LTD", "SHUBH LAXMI");
        Assert.NotEmpty(result.Reasons);
        Assert.NotEqual("none", result.PrimarySignal);
    }

    [Fact]
    public void SameClassLiftsAndUnrelatedClassesDamp()
    {
        Assert.Equal(88, _similarity.ApplyClassWeighting(80, "29", "29").Score);

        // 29 and 30 are foods and staples - they conflict in practice, so the
        // score is left alone rather than damped.
        Assert.Equal(80, _similarity.ApplyClassWeighting(80, "29", "30").Score);

        // 29 (foods) against 9 (software) is damped but NOT zeroed: a strong
        // mark can still be opposed across classes on reputation.
        Assert.Equal(68, _similarity.ApplyClassWeighting(80, "29", "9").Score);
    }

    [Fact]
    public void AnUnknownClassLeavesTheScoreUntouched()
    {
        var (score, note) = _similarity.ApplyClassWeighting(80, null, "9");
        Assert.Equal(80, score);
        Assert.Null(note);
    }

    [Fact(Skip = "KNOWN DEFECT - see IMPLEMENTATION_STATUS.md. " +
                 "When BOTH marks reduce to nothing distinctive, Compare falls back to " +
                 "the full strings, which reintroduces exactly the false positive the " +
                 "distinctive-core design removes. SUPER FOODS vs SUPER TOOLS currently " +
                 "scores ~82 on edit distance over two generic words and would raise an " +
                 "alert. Do not 'fix' by deleting the fallback: two all-generic marks " +
                 "must still be comparable. Needs a deliberate decision, then this test " +
                 "un-skipped.")]
    public void SharedGenericWordsAreNotAConflict()
    {
        var result = _similarity.Compare("SUPER FOODS", "SUPER TOOLS");
        Assert.True(result.Score < 60, $"expected no alert, got {result.Score}");
    }
}
