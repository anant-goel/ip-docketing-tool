using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IPDocketing.Core.Ai;

/// <summary>One model behind one API. Implementations must never throw.</summary>
public interface IAiProvider
{
    AiProviderKind Kind { get; }

    /// <summary>True when a key exists. Says nothing about whether the key works.</summary>
    bool IsConfigured { get; }

    Task<AiResponse> AskAsync(AiRequest request, CancellationToken ct = default);

    /// <summary>
    /// Asks the provider which models this key may use. A cheap GET that costs
    /// nothing and, crucially, exercises authentication WITHOUT exercising the
    /// model name - which is the only way to tell a dead key from a dead model.
    /// </summary>
    Task<AiModelList> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>
/// Shared plumbing. Each provider differs only in its URL, its auth header and
/// the shape of its request and response - so that is all the subclasses supply.
///
/// All three share one HttpClient. Creating an HttpClient per call is the
/// classic way to exhaust sockets on a machine that then looks like it has a
/// network fault.
/// </summary>
public abstract class AiProviderBase : IAiProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    protected readonly AiCredentialStore Credentials;
    protected readonly AiSettings Settings;

    protected AiProviderBase(AiCredentialStore credentials, AiSettings settings)
    {
        Credentials = credentials;
        Settings = settings;
    }

    public abstract AiProviderKind Kind { get; }

    public bool IsConfigured => Credentials.HasKey(Kind);

    protected string Model => Settings.ModelFor(Kind);

    protected abstract HttpRequestMessage BuildRequest(string apiKey, AiRequest request);

    /// <summary>Pulls the answer out of this provider's response shape. Returns null if absent.</summary>
    protected abstract string? ReadAnswer(JsonElement root);

    /// <summary>
    /// Why a 200 response carried no text. Overridable because "it worked but
    /// said nothing" is the least self-explanatory failure there is, and every
    /// provider puts the reason somewhere different.
    /// </summary>
    protected virtual string DescribeEmptyAnswer(JsonElement root) => "The response contained no text.";

    /// <summary>The provider's "what models can this key use" request.</summary>
    protected abstract HttpRequestMessage BuildListModelsRequest(string apiKey);

    /// <summary>Reads that provider's catalogue response shape.</summary>
    protected abstract IReadOnlyList<AiModelInfo> ReadModels(JsonElement root);

    public async Task<AiModelList> ListModelsAsync(CancellationToken ct = default)
    {
        var apiKey = Credentials.GetKey(Kind);
        if (apiKey is null)
            return AiModelList.Fail(Kind, "No API key is configured for this provider.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Settings.TimeoutSeconds)));

            using var httpRequest = BuildListModelsRequest(apiKey);
            using var response = await Http.SendAsync(httpRequest, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
                return AiModelList.Fail(Kind, DescribeFailure(response.StatusCode, body));

            using var parsed = JsonDocument.Parse(body);
            return AiModelList.Ok(Kind, ReadModels(parsed.RootElement));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AiModelList.Fail(Kind, $"Timed out after {Settings.TimeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            return AiModelList.Fail(Kind, ex.Message);
        }
    }

    /// <summary>
    /// A single alternative request to try when the first one failed, or null to
    /// accept the failure.
    ///
    /// No provider currently overrides this. Gemini did, retrying an auth
    /// failure as a bearer token, and that turned out to make the error message
    /// worse rather than the request work - see the note in GeminiProvider. The
    /// hook is kept because the shape is right for a genuine one-shot recovery;
    /// anything added here must be narrow and must not mask the real error.
    /// </summary>
    protected virtual HttpRequestMessage? BuildRecoveryRequest(
        string apiKey, AiRequest request, System.Net.HttpStatusCode status, string body) => null;

    public async Task<AiResponse> AskAsync(AiRequest request, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();

        var apiKey = Credentials.GetKey(Kind);
        if (apiKey is null)
            return AiResponse.Fail(Kind, "No API key is configured for this provider.", clock.Elapsed);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Settings.TimeoutSeconds)));

            using var httpRequest = BuildRequest(apiKey, request);
            using var response = await Http.SendAsync(httpRequest, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            // One recovery attempt, where the provider defines one. Only Gemini
            // does, and only for the AQ-key authentication mess - see there.
            if (!response.IsSuccessStatusCode &&
                BuildRecoveryRequest(apiKey, request, response.StatusCode, body) is { } retry)
            {
                using (retry)
                using (var second = await Http.SendAsync(retry, timeout.Token))
                {
                    var secondBody = await second.Content.ReadAsStringAsync(timeout.Token);

                    if (second.IsSuccessStatusCode)
                    {
                        using var reparsed = JsonDocument.Parse(secondBody);
                        var recovered = ReadAnswer(reparsed.RootElement);

                        if (!string.IsNullOrWhiteSpace(recovered))
                            return AiResponse.Ok(Kind, recovered!.Trim(), clock.Elapsed);
                    }

                    return AiResponse.Fail(Kind, DescribeFailure(second.StatusCode, secondBody), clock.Elapsed);
                }
            }

            if (!response.IsSuccessStatusCode)
                return AiResponse.Fail(Kind, DescribeFailure(response.StatusCode, body), clock.Elapsed);

            using var parsed = JsonDocument.Parse(body);
            var answer = ReadAnswer(parsed.RootElement);

            return string.IsNullOrWhiteSpace(answer)
                ? AiResponse.Fail(Kind, DescribeEmptyAnswer(parsed.RootElement), clock.Elapsed)
                : AiResponse.Ok(Kind, answer!.Trim(), clock.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AiResponse.Fail(Kind, $"Timed out after {Settings.TimeoutSeconds}s.", clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return AiResponse.Fail(Kind, "Cancelled.", clock.Elapsed);
        }
        catch (Exception ex)
        {
            // One provider failing must never take down a run across three.
            return AiResponse.Fail(Kind, ex.Message, clock.Elapsed);
        }
    }

    /// <summary>
    /// Turns a status code into something a user can act on, and deliberately
    /// includes only a short prefix of the body: an error payload can echo the
    /// request back, and this app's requests contain client documents.
    /// </summary>
    private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
    {
        var hint = status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "The API key was rejected. Check it in Settings. " +
                                                      "For Gemini: Google now issues \"auth keys\" beginning " +
                                                      "\"AQ.\", and some accounts get 401 " +
                                                      "ACCESS_TOKEN_TYPE_UNSUPPORTED from every request made " +
                                                      "with one, with no published fix. Old AIzaSy keys still " +
                                                      "work but are themselves rejected from September 2026. " +
                                                      "Use Test to see whether the key or the model is at fault.",
            System.Net.HttpStatusCode.Forbidden => "The key is valid but not permitted to use this model.",
            System.Net.HttpStatusCode.NotFound => "The model name was not recognised - it has probably been " +
                                                  "retired. Use Test in Settings to list the models this key " +
                                                  "can actually reach, and pick one of those.",
            System.Net.HttpStatusCode.TooManyRequests => "Rate limited. Try again shortly.",
            _ => "The provider returned an error.",
        };

        var snippet = body.Length > 200 ? body[..200] + "…" : body;
        return $"{(int)status} {status}: {hint} {snippet}".Trim();
    }

    protected static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>
    /// The system instruction, with an explicit JSON-only demand appended when
    /// the request asked for JSON.
    ///
    /// Belt and braces on purpose. Two of the three providers have a real JSON
    /// mode and it is set in their request shapes below, but Anthropic has no
    /// equivalent flag on the Messages API, and OpenAI's json_object mode
    /// refuses the request outright unless the word "json" appears somewhere in
    /// the messages. One sentence in the system prompt satisfies both.
    /// </summary>
    protected static string SystemFor(AiRequest request) => request.JsonOnly
        ? request.System.TrimEnd() +
          "\n\nReply with a single valid JSON value and nothing else: no explanation, " +
          "no preamble, no markdown code fence."
        : request.System;

    /// <summary>
    /// The caller's raw JSON Schema as a JsonElement, ready to be nested inside
    /// a payload. Deserialize rather than JsonDocument.Parse on purpose: the
    /// element outlives this call, and a JsonElement whose JsonDocument has been
    /// disposed throws when the payload is later serialised.
    ///
    /// Returns null if the schema is absent or is not valid JSON - a malformed
    /// schema degrades the request to plain JSON mode rather than failing it.
    /// </summary>
    protected static JsonElement? SchemaOf(AiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JsonSchema)) return null;

        try { return JsonSerializer.Deserialize<JsonElement>(request.JsonSchema!); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Anthropic Messages API.</summary>
public sealed class AnthropicProvider : AiProviderBase
{
    public AnthropicProvider(AiCredentialStore credentials, AiSettings settings)
        : base(credentials, settings) { }

    public override AiProviderKind Kind => AiProviderKind.Anthropic;

    protected override HttpRequestMessage BuildRequest(string apiKey, AiRequest request)
    {
        // NO temperature. This request used to send one, and on Opus 4.7 and
        // every later model - including the claude-sonnet-5 that is now the
        // default - a non-default temperature is a 400 on EVERY request,
        // thinking or not. The field was quietly turning the whole provider off.
        // Determinism comes from the instruction instead.
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = SystemFor(request),
            ["messages"] = new[] { new { role = "user", content = request.User } },
        };

        // Anthropic's structured outputs, GA and no beta header needed. Note the
        // field is output_config.format - NOT output_format, which was the beta
        // spelling and is only accepted "for a transition period", and not
        // response_format, which is OpenAI's.
        if (SchemaOf(request) is { } schema)
            payload["output_config"] = new
            {
                format = new { type = "json_schema", schema },
            };

        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = Json(payload),
        };

        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return message;
    }

    protected override HttpRequestMessage BuildListModelsRequest(string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return message;
    }

    protected override IReadOnlyList<AiModelInfo> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<AiModelInfo>();

        return data.EnumerateArray()
            .Where(m => m.TryGetProperty("id", out _))
            .Select(m => new AiModelInfo(
                m.GetProperty("id").GetString() ?? "",
                m.TryGetProperty("display_name", out var d) ? d.GetString() : null))
            .Where(m => m.Id.Length > 0)
            .ToList();
    }

    protected override string? ReadAnswer(JsonElement root)
        => root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Concat(content.EnumerateArray()
                .Where(block => block.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(block => block.TryGetProperty("text", out var v) ? v.GetString() : null))
            : null;
}

/// <summary>OpenAI Chat Completions API.</summary>
public sealed class OpenAiProvider : AiProviderBase
{
    public OpenAiProvider(AiCredentialStore credentials, AiSettings settings)
        : base(credentials, settings) { }

    public override AiProviderKind Kind => AiProviderKind.OpenAi;

    protected override HttpRequestMessage BuildRequest(string apiKey, AiRequest request)
    {
        // No temperature, for the same reason as Anthropic: OpenAI's reasoning
        // models - which is what the whole GPT-5 line is - reject it outright
        // with "Only the default (1) value is supported".
        //
        // max_completion_tokens, never max_tokens: max_tokens is deprecated on
        // chat/completions and is a hard 400 on these models.
        //
        // The floor matters. Reasoning tokens are counted against this ceiling
        // before a single visible character is produced, so the 1024 a caller
        // asks for can be spent entirely on thinking and return an EMPTY answer
        // that looks like a broken provider. A ceiling is not a charge - only
        // tokens actually generated are billed - so it costs nothing to leave
        // room.
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["max_completion_tokens"] = Math.Max(request.MaxTokens, 25_000),
            ["messages"] = new[]
            {
                new { role = "system", content = SystemFor(request) },
                new { role = "user", content = request.User },
            },
        };

        // Only sent when asked for. Turning JSON mode on unconditionally would
        // break every ordinary question, since the model would then be obliged
        // to answer "what class is this" as a JSON document.
        if (SchemaOf(request) is { } schema)
        {
            // Structured Outputs. strict:true makes the schema binding rather
            // than advisory - the decoder cannot emit a non-conforming token.
            // The name is constrained to letters, digits, underscore and dash.
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = SafeSchemaName(request.JsonSchemaName),
                    strict = true,
                    schema,
                },
            };
        }
        else if (request.JsonOnly)
        {
            // The older, schema-less mode. Still supported, and still requires
            // the word "json" to appear in the messages - which SystemFor has
            // just guaranteed by appending its JSON-only sentence.
            payload["response_format"] = new { type = "json_object" };
        }

        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = Json(payload),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    /// <summary>OpenAI rejects a schema name outside [A-Za-z0-9_-]{1,64}.</summary>
    private static string SafeSchemaName(string name)
    {
        var cleaned = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (cleaned.Length == 0) cleaned = "extraction";
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    protected override HttpRequestMessage BuildListModelsRequest(string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    protected override IReadOnlyList<AiModelInfo> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<AiModelInfo>();

        return data.EnumerateArray()
            .Where(m => m.TryGetProperty("id", out _))
            .Select(m =>
            {
                var id = m.GetProperty("id").GetString() ?? "";

                // shutdown_date is the single most useful field OpenAI exposes:
                // it says a model is scheduled to stop working before the day it
                // starts returning errors.
                var shutdown = m.TryGetProperty("shutdown_date", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;

                return new AiModelInfo(
                    id, null,
                    shutdown is null ? null : $"shuts down {shutdown}");
            })
            .Where(m => m.Id.Length > 0)
            .ToList();
    }

    protected override string? ReadAnswer(JsonElement root)
        => root.TryGetProperty("choices", out var choices) &&
           choices.ValueKind == JsonValueKind.Array &&
           choices.GetArrayLength() > 0 &&
           choices[0].TryGetProperty("message", out var msg) &&
           msg.TryGetProperty("content", out var content)
            ? content.GetString()
            : null;
}

/// <summary>Google Gemini generateContent API.</summary>
public sealed class GeminiProvider : AiProviderBase
{
    public GeminiProvider(AiCredentialStore credentials, AiSettings settings)
        : base(credentials, settings) { }

    public override AiProviderKind Kind => AiProviderKind.Gemini;

    protected override HttpRequestMessage BuildRequest(string apiKey, AiRequest request)
    {
        // Gemini takes the key as a header rather than a query parameter. A key
        // in the URL ends up in proxy logs and crash reports; a header does not.
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent";

        // Gemini still honours temperature, unlike the other two, so a document
        // extraction really can be pinned to 0 here.
        //
        // The output floor is for the same reason as OpenAI's: Gemini 3 models
        // think before they answer and those tokens are drawn from
        // maxOutputTokens. A tight budget comes back as a 200 with an empty
        // candidate and finishReason MAX_TOKENS - which reads as "the AI
        // returned nothing" when it actually means "the AI was not given room to
        // finish".
        var generationConfig = new Dictionary<string, object>
        {
            ["temperature"] = request.Temperature,
            ["maxOutputTokens"] = Math.Max(request.MaxTokens, 8_192),
        };

        // Gemini's own structured-output switch. Same field the google-genai
        // Python SDK sets as response_mime_type - it is a plain field on
        // generationConfig over REST, so no SDK is needed to reach it. camelCase
        // is the canonical REST spelling; the snake_case form in Google's own
        // curl examples is the proto3 alias and works too.
        if (request.JsonOnly)
            generationConfig["responseMimeType"] = "application/json";

        // responseSchema goes INSIDE generationConfig on :generateContent. The
        // current structured-output guide shows a root-level response_format
        // object instead - that guide has been rewritten for the newer
        // Interactions API and its shape is rejected here.
        //
        // Sanitised on the way in: Gemini accepts an OpenAPI subset, not full
        // JSON Schema, and the very keyword OpenAI's strict mode REQUIRES -
        // additionalProperties: false - is not in that subset. Without this,
        // one schema could not serve all three providers, which is the whole
        // point of passing a schema at all.
        if (SchemaOf(request) is { } schema)
            generationConfig["responseSchema"] = ToGeminiSchema(schema)!;

        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = Json(new
            {
                system_instruction = new { parts = new[] { new { text = SystemFor(request) } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = request.User } } } },
                generationConfig,
            }),
        };

        message.Headers.Add("x-goog-api-key", apiKey);
        return message;
    }

    // THE "AQ." KEY PROBLEM - AND WHY THERE IS NO RETRY HERE ANY MORE.
    //
    // AI Studio now issues "auth keys" beginning "AQ." instead of the old
    // "AIzaSy" standard keys, and many of them come back from generateContent
    // with 401 ACCESS_TOKEN_TYPE_UNSUPPORTED - the endpoint asking for "an OAuth
    // 2 access token, login cookie or other valid authentication credential".
    //
    // This code previously retried such a failure with Authorization: Bearer, on
    // the reasoning that an AQ. key looks like a bearer credential. That was
    // wrong and has been removed. Sending the key as a bearer token is reported
    // to produce 400 "Multiple authentication credentials received", or a 401
    // that says "invalid_api_key" about a perfectly valid key - so the retry
    // replaced an accurate error with a misleading one, which is worse than
    // failing. x-goog-api-key is the documented header for BOTH key types and is
    // what Google's own current examples send.
    //
    // The real state of it, as of August 2026: the AQ. migration is deliberate
    // and Google has published no fix for the 401. Reports include accounts with
    // billing enabled, the API enabled, and a correctly restricted key, still
    // rejected across raw curl, the SDK, the header and the ?key= parameter.
    // Nothing on this side of the wire fixes that.
    //
    // What DOES help is telling the two failures apart, which is why Settings
    // now calls ListModelsAsync first: if a plain GET of the model catalogue
    // with the same key also 401s, the key is affected and no request shape will
    // save it; if the GET succeeds and generateContent fails, the problem is the
    // model name. See AiOrchestrator.TestAsync.

    /// <summary>
    /// JSON Schema keywords Gemini's OpenAPI-subset schema does not define.
    /// Sending them is not ignored - it is a 400 on an otherwise correct call.
    /// </summary>
    private static readonly HashSet<string> UnsupportedSchemaKeys = new(StringComparer.Ordinal)
    {
        "additionalProperties", "$schema", "$id", "title", "default", "examples",
        "strict", "const", "patternProperties",
    };

    /// <summary>
    /// Rewrites a JSON Schema into the subset Gemini accepts, dropping the
    /// keywords above at every level. Structure, types, properties, required
    /// and enums all survive - only the vocabulary Gemini has no opinion about
    /// is removed.
    /// </summary>
    private static object? ToGeminiSchema(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject()
            .Where(p => !UnsupportedSchemaKeys.Contains(p.Name))
            .ToDictionary(p => p.Name, p => ToGeminiSchema(p.Value)),

        JsonValueKind.Array => node.EnumerateArray().Select(ToGeminiSchema).ToList(),
        JsonValueKind.String => node.GetString(),
        JsonValueKind.Number => node.TryGetInt64(out var whole) ? whole : node.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    protected override HttpRequestMessage BuildListModelsRequest(string apiKey)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");

        message.Headers.Add("x-goog-api-key", apiKey);
        return message;
    }

    protected override IReadOnlyList<AiModelInfo> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return Array.Empty<AiModelInfo>();

        return models.EnumerateArray()
            .Select(m =>
            {
                // Gemini reports "models/gemini-3.5-flash"; requests take the
                // bare id. Storing the prefixed form in Settings would 404.
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var id = name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name;

                // Only models that can actually answer a generateContent call.
                // The catalogue also lists embedding, TTS, video and image
                // models, and picking one of those looks like a broken key.
                var supported = m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                                methods.ValueKind == JsonValueKind.Array &&
                                methods.EnumerateArray().Any(x =>
                                    string.Equals(x.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase));

                return (id, supported,
                        display: m.TryGetProperty("displayName", out var d) ? d.GetString() : null);
            })
            .Where(x => x.supported && x.id.Length > 0)
            .Select(x => new AiModelInfo(x.id, x.display))
            .ToList();
    }

    /// <summary>
    /// Gemini answers "why is this empty" in two places, and neither is obvious
    /// from a blank string: promptFeedback.blockReason when the INPUT was
    /// refused, and candidates[0].finishReason when the OUTPUT stopped early.
    /// </summary>
    protected override string DescribeEmptyAnswer(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var blocked))
        {
            return $"Gemini refused the request itself (blockReason {blocked.GetString()}). " +
                   "The document text, not the model, is what it objected to.";
        }

        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("finishReason", out var reason))
        {
            var value = reason.GetString();

            return value switch
            {
                "MAX_TOKENS" => "Gemini used its entire output budget on internal reasoning and " +
                                "produced no answer. Ask for fewer fields at once, or raise Max tokens.",
                "SAFETY" or "PROHIBITED_CONTENT" =>
                    $"Gemini stopped on a content filter ({value}).",
                "RECITATION" => "Gemini stopped because the answer reproduced source text too closely.",
                _ => $"The response contained no text (finishReason {value}).",
            };
        }

        return "The response contained no text.";
    }

    protected override string? ReadAnswer(JsonElement root)
        => root.TryGetProperty("candidates", out var candidates) &&
           candidates.ValueKind == JsonValueKind.Array &&
           candidates.GetArrayLength() > 0 &&
           candidates[0].TryGetProperty("content", out var content) &&
           content.TryGetProperty("parts", out var parts) &&
           parts.ValueKind == JsonValueKind.Array
            ? string.Concat(parts.EnumerateArray()
                .Select(part => part.TryGetProperty("text", out var v) ? v.GetString() : null))
            : null;
}
