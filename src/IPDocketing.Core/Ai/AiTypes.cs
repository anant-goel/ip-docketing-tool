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
    double Temperature = 0.0,
    bool JsonOnly = false,
    string? JsonSchema = null,
    string JsonSchemaName = "extraction")
{
    /// <summary>
    /// Temperature defaults to zero. Everything this app asks a model - which
    /// class is this, what date is this, what does this examination report
    /// require - has one right answer, and sampling variety into it only makes
    /// two providers disagree for reasons that are not about the document.
    ///
    /// NOTE: only Gemini is actually sent this value. Anthropic returns 400 for
    /// any non-default temperature on Opus 4.7 and later, and OpenAI does the
    /// same on its reasoning models ("Only the default (1) value is supported"),
    /// so both providers omit the field entirely rather than fail every request.
    /// Determinism there comes from the instruction, not the sampler.
    /// </summary>
    public static AiRequest Extract(string instruction, string documentText, int maxTokens = 1024)
        => new(instruction, documentText, maxTokens);

    /// <summary>
    /// Same as Extract, but the model is put into JSON mode where the provider
    /// supports one.
    ///
    /// This matters more here than it looks. Pulling several fields out of an
    /// examination report at once - class, deadline, objections, agent - means
    /// parsing the answer, and a model left in prose mode will wrap perfectly
    /// good JSON in "Here is the information you asked for:" and a fenced code
    /// block roughly half the time. Worse, for consensus: three providers can
    /// extract identical data and still be scored as a three-way disagreement
    /// because one of them added a preamble. JSON mode removes that whole class
    /// of false conflict.
    ///
    /// The instruction must still say what shape the JSON should have; JSON mode
    /// only guarantees it is valid JSON, not that it has the keys you wanted.
    /// </summary>
    public static AiRequest ExtractJson(string instruction, string documentText, int maxTokens = 1024)
        => new(instruction, documentText, maxTokens, JsonOnly: true);

    /// <summary>
    /// The strongest form: the provider is given the actual JSON Schema and
    /// constrains its decoding to it, so the reply cannot come back with a
    /// missing key, an extra key, or a number where a string was expected.
    ///
    /// All three providers now support this, each spelling it differently -
    /// Anthropic output_config.format, OpenAI response_format.json_schema with
    /// strict, Gemini generationConfig.responseSchema. The provider classes
    /// handle the spelling; callers pass one schema.
    ///
    /// <paramref name="jsonSchema"/> is raw JSON - an object schema, i.e.
    /// {"type":"object","properties":{...},"required":[...]}. For OpenAI's
    /// strict mode every property must appear in "required" and the schema must
    /// set "additionalProperties": false, so write it that way and all three are
    /// satisfied.
    /// </summary>
    public static AiRequest ExtractSchema(
        string instruction, string documentText, string jsonSchema,
        string schemaName = "extraction", int maxTokens = 1024)
        => new(instruction, documentText, maxTokens,
               JsonOnly: true, JsonSchema: jsonSchema, JsonSchemaName: schemaName);
}

/// <summary>One model as the provider reports it, from its own catalogue.</summary>
public sealed record AiModelInfo(string Id, string? DisplayName = null, string? Note = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) || DisplayName == Id
        ? Id
        : $"{Id} ({DisplayName})";
}

/// <summary>
/// The result of asking a provider what models the installed key can use.
///
/// Worth its own type because it answers a question no chat call can: a failure
/// here is about the KEY, and a failure on a chat call with this succeeding is
/// about the MODEL NAME. Without that split, a retired model and a rejected key
/// are the same red text.
/// </summary>
public sealed record AiModelList(
    AiProviderKind Provider,
    bool Succeeded,
    IReadOnlyList<AiModelInfo> Models,
    string? Error)
{
    public static AiModelList Ok(AiProviderKind provider, IReadOnlyList<AiModelInfo> models)
        => new(provider, true, models, null);

    public static AiModelList Fail(AiProviderKind provider, string error)
        => new(provider, false, Array.Empty<AiModelInfo>(), error);

    public bool Contains(string modelId) =>
        Models.Any(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What "Test" in Settings found out. Deliberately more than a pass/fail: the
/// two things that break an AI integration in practice - a key the provider
/// will not accept, and a model name that has been retired since it was typed -
/// look identical from a single failed chat call, and the user cannot fix
/// either one without being told which it is.
/// </summary>
public sealed record AiDiagnostic(
    AiProviderKind Provider,
    bool KeyAccepted,
    bool ModelAccepted,
    IReadOnlyList<AiModelInfo> AvailableModels,
    string Summary)
{
    public bool Succeeded => KeyAccepted && ModelAccepted;
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
    /// code - which is exactly why Models overrides them, and why Settings can
    /// now list what a key can actually reach.
    ///
    /// Checked against all three providers' published catalogues in August 2026.
    /// Every one of the previous defaults was wrong or about to be:
    ///
    ///   gemini-2.0-flash    shut down 1 June 2026.
    ///   gemini-flash-latest an alias, and aliases move underneath you. It is
    ///                       documented as pointing at 3.5 Flash, but 3.6 and
    ///                       3.7 Flash both went GA afterwards with no published
    ///                       re-point, so what it resolves to today is a guess.
    ///                       Pinned to gemini-3.5-flash instead - the model
    ///                       Google itself names as the 2.0 Flash replacement.
    ///   claude-sonnet-4-5   still live, but its tentative retirement is 29
    ///                       September 2026 - about five weeks from this change.
    ///                       Moved to claude-sonnet-5.
    ///   gpt-4o              the 4o family is being retired through October
    ///                       2026. Moved to gpt-5.6-luna, which is the cheap
    ///                       high-volume extraction model and supports strict
    ///                       structured outputs.
    ///
    /// None of these auto-upgrade. Anthropic has no -latest form at all, and a
    /// dateless Anthropic ID from 4.6 onward is a pinned snapshot rather than an
    /// alias. That is a feature for docketing: the model that read a document in
    /// January reads it the same way in December.
    /// </summary>
    public static string DefaultModel(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Anthropic => "claude-sonnet-5",
        AiProviderKind.OpenAi => "gpt-5.6-luna",
        AiProviderKind.Gemini => "gemini-3.5-flash",
        _ => string.Empty,
    };
}
