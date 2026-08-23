namespace IPDocketing.Core.Ai;

/// <summary>The cloud providers this app can talk to.</summary>
public enum AiProviderKind
{
    Anthropic,
    OpenAi,
    Gemini,
}

/// <summary>
/// One question put to a model.
///
/// <paramref name="System"/> carries the instruction, <paramref name="User"/> the
/// material. They are kept apart rather than concatenated because every provider
/// has its own place to put a system instruction, and merging them into one blob
/// measurably weakens all three.
/// </summary>
public sealed record AiRequest(
    string System,
    string User,
    int MaxTokens = 1024,
    double Temperature = 0.0)
{
    /// <summary>
    /// Temperature defaults to zero. Everything this app asks a model - which
    /// class is this, what date is this, what does this examination report
    /// require - has one right answer, and sampling variety into it only makes
    /// two providers disagree for reasons that are not about the document.
    /// </summary>
    public static AiRequest Extract(string instruction, string documentText, int maxTokens = 1024)
        => new(instruction, documentText, maxTokens);
}

/// <summary>What one provider said, or why it could not answer.</summary>
public sealed record AiResponse(
    AiProviderKind Provider,
    bool Succeeded,
    string? Text,
    string? Error,
    TimeSpan Duration)
{
    public static AiResponse Ok(AiProviderKind provider, string text, TimeSpan duration)
        => new(provider, true, text, null, duration);

    public static AiResponse Fail(AiProviderKind provider, string error, TimeSpan duration)
        => new(provider, false, null, error, duration);
}

/// <summary>
/// The combined result of asking every configured provider the same question.
///
/// This deliberately does NOT collapse to a single answer and hide the rest.
/// Where models agree you have corroboration; where they disagree you have a
/// document that needs a person to look at it. Throwing away the disagreement
/// would discard the most valuable thing running three models buys you, and in
/// a docketing system a confidently wrong date is worse than an obvious
/// question mark.
/// </summary>
public sealed record AiConsensus(
    IReadOnlyList<AiResponse> Responses,
    string? AgreedText,
    int AgreeingProviders,
    int RespondingProviders,
    bool NeedsReview,
    string Explanation)
{
    public IEnumerable<AiResponse> Succeeded => Responses.Where(r => r.Succeeded);
    public IEnumerable<AiResponse> Failed => Responses.Where(r => !r.Succeeded);

    /// <summary>True when nothing answered at all - distinct from "they disagreed".</summary>
    public bool NothingAnswered => RespondingProviders == 0;
}

/// <summary>
/// User-controlled AI configuration. Persisted next to the keys, but separate
/// from them: settings are readable, keys are not.
/// </summary>
public sealed class AiSettings
{
    /// <summary>Master switch. Off means no provider is ever called.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Explicit, separate consent to send document text to a third-party
    /// service. Deliberately NOT implied by adding a key.
    ///
    /// This app holds other people's confidential trademark files. Pasting an
    /// API key is a statement about billing; sending a client's examination
    /// report to Anthropic, OpenAI or Google is a statement about
    /// confidentiality, and the second is not evidence of the first. The
    /// orchestrator refuses to transmit anything until this is set.
    /// </summary>
    public bool CloudConsentGiven { get; set; }

    /// <summary>Which providers to include in a run, where a key exists for them.</summary>
    public HashSet<AiProviderKind> ActiveProviders { get; set; } = new();

    /// <summary>
    /// Model per provider. Editable because model names change far more often
    /// than this application will be rebuilt, and a hardcoded name that has been
    /// retired looks exactly like a broken integration.
    /// </summary>
    public Dictionary<string, string> Models { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 60;

    public string ModelFor(AiProviderKind provider) =>
        Models.TryGetValue(provider.ToString(), out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : DefaultModel(provider);

    /// <summary>
    /// Starting points only. Whether any of these still exists is a question
    /// about the provider's catalogue on the day you read this, not about this
    /// code - which is exactly why Models overrides them.
    /// </summary>
    public static string DefaultModel(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Anthropic => "claude-sonnet-4-5",
        AiProviderKind.OpenAi => "gpt-4o",
        AiProviderKind.Gemini => "gemini-2.0-flash",
        _ => string.Empty,
    };
}
