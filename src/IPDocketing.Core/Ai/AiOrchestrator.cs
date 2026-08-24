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

    /// <summary>Asks one provider which models the installed key can reach.</summary>
    public Task<AiModelList> ListModelsAsync(AiProviderKind kind, CancellationToken ct = default)
        => _providers.First(p => p.Kind == kind).ListModelsAsync(ct);

    /// <summary>
    /// Checks a provider in two stages and reports which one failed. Bypasses
    /// the consent gate on purpose: it transmits a fixed word, never a client
    /// document.
    ///
    /// WHY TWO STAGES
    ///
    /// A single chat call cannot distinguish the two things that actually go
    /// wrong. A rejected key and a retired model name both come back as one line
    /// of red text, and the user's next move is completely different in each
    /// case - replace the key, or replace the model. Guessing wrong wastes an
    /// afternoon.
    ///
    /// So: list the catalogue first. That is a GET, it costs nothing, and it
    /// exercises authentication WITHOUT naming a model. If it fails, the key is
    /// the problem and the model name is irrelevant. If it succeeds, the key is
    /// good, and the configured model can be checked against the returned list
    /// BEFORE spending a request on it - so a retired name is reported as a
    /// retired name, with the live alternatives listed.
    /// </summary>
    public async Task<AiDiagnostic> TestAsync(AiProviderKind kind, CancellationToken ct = default)
    {
        var provider = _providers.First(p => p.Kind == kind);
        var model = Settings.ModelFor(kind);

        if (!provider.IsConfigured)
            return new AiDiagnostic(kind, false, false, Array.Empty<AiModelInfo>(),
                $"No {kind} API key is configured. Paste one into the box and press Save first.");

        // Stage one: is the key any good at all?
        var catalogue = await provider.ListModelsAsync(ct);

        if (!catalogue.Succeeded)
            return new AiDiagnostic(kind, false, false, Array.Empty<AiModelInfo>(),
                $"{kind} rejected the key before any model was named, so the model in the box is " +
                $"not the problem. {catalogue.Error}");

        // Stage two: does the model in Settings still exist?
        if (!catalogue.Contains(model))
        {
            var suggestions = Suggest(catalogue.Models, model);

            return new AiDiagnostic(kind, true, false, catalogue.Models,
                $"The {kind} key works - {catalogue.Models.Count} models are available to it - but " +
                $"\"{model}\" is not one of them. It has most likely been retired. " +
                (suggestions.Length > 0
                    ? $"Try one of these instead: {string.Join(", ", suggestions)}."
                    : "Check the provider's model list for a current name."));
        }

        // Both look right, so actually spend one tiny request proving it.
        var answer = await provider.AskAsync(
            new AiRequest("Reply with the single word OK.", "OK?", MaxTokens: 16), ct);

        return answer.Succeeded
            ? new AiDiagnostic(kind, true, true, catalogue.Models,
                $"{kind} answered in {answer.Duration.TotalMilliseconds:0} ms using {model}. " +
                $"Key and model both work; {catalogue.Models.Count} models are available to this key.")
            : new AiDiagnostic(kind, true, false, catalogue.Models,
                $"The {kind} key works and \"{model}\" is in its model list, but the request itself " +
                $"failed: {answer.Error}");
    }

    /// <summary>
    /// A few plausible replacements for a model name that no longer exists.
    ///
    /// Prefers names sharing the configured model's family prefix, so a dead
    /// "gemini-2.0-flash" suggests other flash models rather than the first
    /// three entries of an alphabetical list.
    /// </summary>
    private static string[] Suggest(IReadOnlyList<AiModelInfo> models, string wanted)
    {
        var stem = new string(wanted.TakeWhile(c => c != '-').ToArray());

        var family = wanted.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "flash"
                   : wanted.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ? "sonnet"
                   : wanted.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? "haiku"
                   : wanted.Contains("opus", StringComparison.OrdinalIgnoreCase) ? "opus"
                   : wanted.Contains("mini", StringComparison.OrdinalIgnoreCase) ? "mini"
                   : null;

        var ranked = models
            .Select(m => m.Id)
            .OrderByDescending(id => family is not null && id.Contains(family, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(id => stem.Length > 2 && id.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();

        return ranked;
    }
}
