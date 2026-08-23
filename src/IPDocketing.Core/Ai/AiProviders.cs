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
            System.Net.HttpStatusCode.Unauthorized => "The API key was rejected. Check it in Settings.",
            System.Net.HttpStatusCode.Forbidden => "The key is valid but not permitted to use this model.",
            System.Net.HttpStatusCode.NotFound => "The model name was not recognised. Check the model in Settings.",
            System.Net.HttpStatusCode.TooManyRequests => "Rate limited. Try again shortly.",
            _ => "The provider returned an error.",
        };

        var snippet = body.Length > 200 ? body[..200] + "…" : body;
        return $"{(int)status} {status}: {hint} {snippet}".Trim();
    }

    protected static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
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
                system = request.System,
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
        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = Json(new
            {
                model = Model,
                temperature = request.Temperature,
                max_completion_tokens = request.MaxTokens,
                messages = new[]
                {
                    new { role = "system", content = request.System },
                    new { role = "user", content = request.User },
                },
            }),
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

        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = Json(new
            {
                system_instruction = new { parts = new[] { new { text = request.System } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = request.User } } } },
                generationConfig = new
                {
                    temperature = request.Temperature,
                    maxOutputTokens = request.MaxTokens,
                },
            }),
        };

        message.Headers.Add("x-goog-api-key", apiKey);
        return message;
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
