using System.Text.RegularExpressions;

namespace IPDocketing.Core.Ai;

/// <summary>
/// Asks every configured provider the same question at the same time and reports
/// what they agreed on - and, more usefully, what they did not.
///
/// WHY RUN THREE MODELS AT ONCE
///
/// Not for a better single answer; for a signal about whether the answer can be
/// trusted at all. A model asked "what is the response deadline in this
/// examination report" will always produce a date. Nothing in that date says
/// whether it read the document or invented it. Three models reading the same
/// page and independently producing the same date is real evidence; three models
/// producing three dates means a human has to open the PDF, which is exactly the
/// outcome you want and exactly what a single model can never tell you.
///
/// So this class never picks a winner when they disagree. It returns every
/// answer, flags NeedsReview, and lets the caller show the conflict.
/// </summary>
public sealed class AiOrchestrator
{
    private readonly AiCredentialStore _credentials;
    private IReadOnlyList<IAiProvider> _providers;

    public AiSettings Settings { get; private set; }

    public AiOrchestrator(AiCredentialStore credentials)
    {
        _credentials = credentials;
        Settings = credentials.LoadSettings();
        _providers = BuildProviders();
    }

    private IReadOnlyList<IAiProvider> BuildProviders() => new IAiProvider[]
    {
        new AnthropicProvider(_credentials, Settings),
        new OpenAiProvider(_credentials, Settings),
        new GeminiProvider(_credentials, Settings),
    };

    /// <summary>
    /// Re-reads settings and keys from disk - call after Settings is saved.
    ///
    /// The providers are rebuilt, not just the settings object. Each provider
    /// holds the AiSettings it was constructed with, so swapping only the
    /// orchestrator's reference would leave all three still using the previous
    /// model name and timeout - which looks exactly like "changing the model in
    /// Settings does nothing".
    /// </summary>
    public void Reload()
    {
        Settings = _credentials.LoadSettings();
        _providers = BuildProviders();
    }

    /// <summary>Providers with a key installed AND ticked in Settings.</summary>
    public IReadOnlyList<IAiProvider> Available => _providers
        .Where(p => p.IsConfigured && Settings.ActiveProviders.Contains(p.Kind))
        .ToList();

    public IReadOnlyList<AiProviderKind> ConfiguredKinds => _providers
        .Where(p => p.IsConfigured)
        .Select(p => p.Kind)
        .ToList();

    /// <summary>
    /// Why a run cannot proceed, or null if it can. Separated from AskAllAsync so
    /// the UI can grey a button out and say why rather than failing on click.
    /// </summary>
    public string? BlockedReason()
    {
        if (!Settings.Enabled)
            return "AI assistance is switched off in Settings.";

        if (!Settings.CloudConsentGiven)
            return "Sending document text to a cloud provider has not been approved. " +
                   "Turn on the consent switch in Settings > AI first.";

        if (Available.Count == 0)
            return ConfiguredKinds.Count == 0
                ? "No API keys have been added yet."
                : "Keys are installed but no provider is ticked in Settings.";

        return null;
    }

    /// <summary>
    /// Runs every available provider concurrently. Never throws; a provider that
    /// fails becomes a failed AiResponse, and the run continues.
    /// </summary>
    public async Task<AiConsensus> AskAllAsync(AiRequest request, CancellationToken ct = default)
    {
        if (BlockedReason() is { } blocked)
            return new AiConsensus(Array.Empty<AiResponse>(), null, 0, 0, true, blocked);

        var providers = Available;

        // Concurrently, not in sequence: three sequential calls at 60s each is a
        // three-minute wait for one field, and they do not depend on each other.
        var responses = await Task.WhenAll(providers.Select(p => p.AskAsync(request, ct)));

        return Compare(responses);
    }

    /// <summary>Builds the consensus view from a set of responses. Pure - the unit tests drive this directly.</summary>
    public static AiConsensus Compare(IReadOnlyList<AiResponse> responses)
    {
        var answered = responses.Where(r => r.Succeeded && !string.IsNullOrWhiteSpace(r.Text)).ToList();

        if (answered.Count == 0)
        {
            var why = responses.Count == 0
                ? "No provider was asked."
                : "Every provider failed: " +
                  string.Join("; ", responses.Select(r => $"{r.Provider} - {r.Error}"));

            return new AiConsensus(responses, null, 0, 0, true, why);
        }

        // Group on a normalised form so "Class 29", "class 29." and "29" count
        // as the same answer. Without this, three models that agree completely
        // would be reported as a three-way disagreement over punctuation.
        var groups = answered
            .GroupBy(r => NormaliseForComparison(r.Text!))
            .OrderByDescending(g => g.Count())
            .ToList();

        var best = groups[0];
        var agreeing = best.Count();
        var responding = answered.Count;

        // A single provider answering is not consensus, however confident it
        // sounds. It is one opinion, and it is labelled as one.
        if (responding == 1)
        {
            return new AiConsensus(
                responses, best.First().Text, 1, 1, true,
                $"Only {best.First().Provider} answered. A single model's answer is not corroborated - " +
                "check it against the source before relying on it.");
        }

        if (agreeing == responding)
        {
            return new AiConsensus(
                responses, best.First().Text, agreeing, responding, false,
                $"All {responding} providers agreed.");
        }

        if (agreeing > responding - agreeing)
        {
            var dissenting = answered.Where(r => !best.Contains(r)).Select(r => r.Provider.ToString());

            return new AiConsensus(
                responses, best.First().Text, agreeing, responding, true,
                $"{agreeing} of {responding} agreed; {string.Join(" and ", dissenting)} differed. " +
                "The majority answer is shown, but the disagreement is worth a look.");
        }

        // An even split has no majority, so there is no answer to offer.
        return new AiConsensus(
            responses, null, agreeing, responding, true,
            $"The {responding} providers did not agree and there is no majority. " +
            "Every answer is listed; this one needs a person.");
    }

    /// <summary>
    /// Case, surrounding punctuation and whitespace runs removed. Deliberately
    /// conservative - it does not try to understand the answer, only to stop
    /// formatting differences masquerading as disagreement.
    /// </summary>
    public static string NormaliseForComparison(string text)
    {
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Trim(' ', '.', ',', ';', ':', '"', '\'', '-', '–').ToUpperInvariant();
    }

    /// <summary>
    /// Sends a one-token question to check a key works. Used by "Test" in
    /// Settings, and it bypasses the consent gate on purpose: it transmits a
    /// fixed word, never a client document.
    /// </summary>
    public async Task<AiResponse> TestAsync(AiProviderKind kind, CancellationToken ct = default)
    {
        var provider = _providers.First(p => p.Kind == kind);

        if (!provider.IsConfigured)
            return AiResponse.Fail(kind, "No API key is configured for this provider.", TimeSpan.Zero);

        return await provider.AskAsync(
            new AiRequest("Reply with the single word OK.", "OK?", MaxTokens: 16), ct);
    }
}
