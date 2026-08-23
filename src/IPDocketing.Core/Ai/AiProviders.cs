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
    /// A single alternative request to try when the first one failed, or null to
    /// accept the failure. Exists for one real case; see GeminiProvider.
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
                ? AiResponse.Fail(Kind, "The response contained no text.", clock.Elapsed)
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
                                                      "For Gemini: keys beginning \"AQ.\" are known to be " +
                                                      "refused by generateContent on some accounts, with no " +
                                                      "fix published - an AIzaSy key works if you can still " +
                                                      "get one issued.",
            System.Net.HttpStatusCode.Forbidden => "The key is valid but not permitted to use this model.",
            System.Net.HttpStatusCode.NotFound => "The model name was not recognised - it has probably been " +
                                                  "retired. Change the model in Settings (an alias such as " +
                                                  "gemini-flash-latest keeps working across retirements).",
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
}

/// <summary>Anthropic Messages API.</summary>
public sealed class AnthropicProvider : AiProviderBase
{
    public AnthropicProvider(AiCredentialStore credentials, AiSettings settings)
        : base(credentials, settings) { }

    public override AiProviderKind Kind => AiProviderKind.Anthropic;

    protected override HttpRequestMessage BuildRequest(string apiKey, AiRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = Json(new
            {
                model = Model,
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                system = SystemFor(request),
                messages = new[] { new { role = "user", content = request.User } },
            }),
        };

        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return message;
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
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["temperature"] = request.Temperature,
            ["max_completion_tokens"] = request.MaxTokens,
            ["messages"] = new[]
            {
                new { role = "system", content = SystemFor(request) },
                new { role = "user", content = request.User },
            },
        };

        // Only sent when asked for. Turning JSON mode on unconditionally would
        // break every ordinary question, since the model would then be obliged
        // to answer "what class is this" as a JSON document.
        if (request.JsonOnly)
            payload["response_format"] = new { type = "json_object" };

        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = Json(payload),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
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

        var generationConfig = new Dictionary<string, object>
        {
            ["temperature"] = request.Temperature,
            ["maxOutputTokens"] = request.MaxTokens,
        };

        // Gemini's own structured-output switch. Same field the google-genai
        // Python SDK sets as response_mime_type - it is a plain field on
        // generationConfig over REST, so no SDK is needed to reach it.
        if (request.JsonOnly)
            generationConfig["responseMimeType"] = "application/json";

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

    /// <summary>
    /// THE "AQ." KEY PROBLEM.
    ///
    /// AI Studio now issues keys beginning "AQ." instead of the long-standing
    /// "AIzaSy" ones, and a lot of them come back from generateContent with
    /// 401 ACCESS_TOKEN_TYPE_UNSUPPORTED - the endpoint replying that it wanted
    /// "an OAuth 2 access token, login cookie or other valid authentication
    /// credential" rather than an API key. Google's position is that the key
    /// format is not itself the cause; there is no published fix.
    ///
    /// x-goog-api-key is the documented way to send an API key and stays the
    /// first attempt. But since the endpoint is asking for a bearer credential,
    /// and an AQ. key looks far more like one than AIzaSy did, it is worth
    /// exactly one retry as a bearer token before giving up.
    ///
    /// Deliberately narrow: only on 401/403, only when the body names that
    /// error, and only once. A blind retry on every failure would turn one bad
    /// request into two and make rate limits worse.
    /// </summary>
    protected override HttpRequestMessage? BuildRecoveryRequest(
        string apiKey, AiRequest request, System.Net.HttpStatusCode status, string body)
    {
        var authFailure = status is System.Net.HttpStatusCode.Unauthorized
                                 or System.Net.HttpStatusCode.Forbidden;

        var looksLikeTokenTypeIssue =
            body.Contains("ACCESS_TOKEN_TYPE_UNSUPPORTED", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("OAuth 2 access token", StringComparison.OrdinalIgnoreCase);

        if (!authFailure || !looksLikeTokenTypeIssue) return null;

        var retry = BuildRequest(apiKey, request);
        retry.Headers.Remove("x-goog-api-key");
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return retry;
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
