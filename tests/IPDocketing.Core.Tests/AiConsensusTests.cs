using IPDocketing.Core.Ai;
using Xunit;

namespace IPDocketing.Core.Tests;

public class AiConsensusTests
{
    private static AiResponse Ok(AiProviderKind kind, string text)
        => AiResponse.Ok(kind, text, TimeSpan.FromMilliseconds(10));

    private static AiResponse Fail(AiProviderKind kind, string error)
        => AiResponse.Fail(kind, error, TimeSpan.FromMilliseconds(10));

    [Fact]
    public void UnanimousProvidersNeedNoReview()
    {
        var consensus = AiOrchestrator.Compare(new[]
        {
            Ok(AiProviderKind.Anthropic, "29"),
            Ok(AiProviderKind.OpenAi, "29"),
            Ok(AiProviderKind.Gemini, "29"),
        });

        Assert.Equal("29", consensus.AgreedText);
        Assert.Equal(3, consensus.AgreeingProviders);
        Assert.False(consensus.NeedsReview);
    }

    [Fact]
    public void FormattingDifferencesAreNotDisagreement()
    {
        // Three models that agree completely would otherwise be reported as a
        // three-way split over a full stop and a capital letter.
        var consensus = AiOrchestrator.Compare(new[]
        {
            Ok(AiProviderKind.Anthropic, "Class 29"),
            Ok(AiProviderKind.OpenAi, "class 29."),
            Ok(AiProviderKind.Gemini, "  Class  29  "),
        });

        Assert.False(consensus.NeedsReview);
        Assert.Equal(3, consensus.AgreeingProviders);
    }

    [Fact]
    public void AMajorityIsReportedButStillFlagged()
    {
        var consensus = AiOrchestrator.Compare(new[]
        {
            Ok(AiProviderKind.Anthropic, "09/07/2026"),
            Ok(AiProviderKind.OpenAi, "09/07/2026"),
            Ok(AiProviderKind.Gemini, "07/09/2026"),
        });

        Assert.Equal("09/07/2026", consensus.AgreedText);
        Assert.Equal(2, consensus.AgreeingProviders);
        Assert.Equal(3, consensus.RespondingProviders);

        // Two out of three on a docketing date is not good enough to act on
        // unseen. The dissenter is named in the explanation.
        Assert.True(consensus.NeedsReview);
        Assert.Contains("Gemini", consensus.Explanation);
    }

    [Fact]
    public void AnEvenSplitOffersNoAnswerAtAll()
    {
        var consensus = AiOrchestrator.Compare(new[]
        {
            Ok(AiProviderKind.Anthropic, "12/08/2026"),
            Ok(AiProviderKind.OpenAi, "18/08/2026"),
        });

        // Picking one of two would be a coin toss presented as a finding.
        Assert.Null(consensus.AgreedText);
        Assert.True(consensus.NeedsReview);
    }

    [Fact]
    public void ASingleAnswerIsNotTreatedAsCorroborated()
    {
        var consensus = AiOrchestrator.Compare(new[]
        {
            Ok(AiProviderKind.Anthropic, "29"),
            Fail(AiProviderKind.OpenAi, "401 Unauthorized"),
            Fail(AiProviderKind.Gemini, "Timed out"),
        });

        Assert.Equal("29", consensus.AgreedText);
        Assert.Equal(1, consensus.RespondingProviders);

        // One model answering is one opinion. The whole point of running three
        // is lost if a lone survivor is reported as agreement.
        Assert.True(consensus.NeedsReview);
    }

    [Fact]
    public void TotalFailureIsDistinctFromDisagreement()
    {
        var consensus = AiOrchestrator.Compare(new[]
        {
            Fail(AiProviderKind.Anthropic, "401 Unauthorized"),
            Fail(AiProviderKind.OpenAi, "Rate limited"),
        });

        Assert.True(consensus.NothingAnswered);
        Assert.Null(consensus.AgreedText);
        Assert.True(consensus.NeedsReview);

        // The reason each one failed has to survive to the UI, or the user is
        // told "no answer" with no way to find out why.
        Assert.Contains("401", consensus.Explanation);
        Assert.Contains("Rate limited", consensus.Explanation);
    }

    [Fact]
    public void AnEmptyRunIsNotAnAgreement()
    {
        var consensus = AiOrchestrator.Compare(Array.Empty<AiResponse>());

        Assert.True(consensus.NothingAnswered);
        Assert.True(consensus.NeedsReview);
        Assert.Null(consensus.AgreedText);
    }

    [Fact]
    public void BlankTextCountsAsNoAnswer()
    {
        // A provider that returns 200 with an empty body must not be counted as
        // a vote, or two real answers become a "majority of three".
        var consensus = AiOrchestrator.Compare(new[]
        {
            Ok(AiProviderKind.Anthropic, "29"),
            Ok(AiProviderKind.OpenAi, "   "),
        });

        Assert.Equal(1, consensus.RespondingProviders);
    }

    [Theory]
    [InlineData("Class 29.", "CLASS 29")]
    [InlineData("  spaced   out  ", "SPACED OUT")]
    [InlineData("\"quoted\"", "QUOTED")]
    public void NormalisationStripsOnlyPresentation(string input, string expected)
        => Assert.Equal(expected, AiOrchestrator.NormaliseForComparison(input));
}
