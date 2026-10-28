using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

/// <summary>
/// ANY OpenAI-compatible /chat/completions endpoint: OpenAI, Azure OpenAI
/// ({endpoint}/openai/v1), Ollama, LM Studio, vLLM... Key from secure config —
/// never hard-coded, never logged. Transient failures retried with backoff.
/// </summary>
public sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    public string ProviderName => "OpenAiCompatible";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _options;
    private readonly ResilienceOptions _resilience;
    private readonly string? _apiKey;

    public OpenAiCompatibleChatProvider(
        HttpClient http,
        OpenAiCompatibleOptions options,
        ResilienceOptions resilience,
        string? apiKey)
    {
        _http = http;
        _options = options;
        // Provider-scoped timeout override: local CPU inference (llama.cpp/Ollama)
        // can legitimately exceed the global default; hosted vendors inherit the
        // global billing-guard semantics unchanged (timeout = ambiguous, never
        // auto-retried). Only TimeoutSeconds is overridden — retry/failover
        // semantics pass through untouched.
        _resilience = options.TimeoutSeconds is int t
            ? new ResilienceOptions
            {
                TimeoutSeconds = t,
                MaxRetries = resilience.MaxRetries,
                BaseDelayMs = resilience.BaseDelayMs,
                RetryAmbiguousFailures = resilience.RetryAmbiguousFailures,
                AllowAmbiguousKeyFallback = resilience.AllowAmbiguousKeyFallback,
                AllowQuotaKeyFallback = resilience.AllowQuotaKeyFallback
            }
            : resilience;
        _apiKey = apiKey;
    }

    public async Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var doc = await PostChatAsync(history, options, cancellationToken).ConfigureAwait(false);
        var text = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        if (_options.StripThinkTags) text = ThinkTagFilter.Strip(text);
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        return new ChatResponse(text, ProviderName, model ?? options?.ModelOverride ?? _options.Model);
    }

    /// <summary>
    /// Real incremental SSE streaming (<c>"stream": true</c>): each delta is
    /// yielded as it arrives; always ends with an IsFinal chunk. Retry/billing
    /// discipline: the request is retried per ResilienceOptions only until
    /// headers arrive; once streaming has started, failures surface — they are
    /// never silently retried (partial output may already be shown).
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpResponse = await PostChatStreamingAsync(history, options, cancellationToken)
            .ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
            throw await ProviderResilience.ToCallExceptionAsync(httpResponse, "Chat backend", cancellationToken)
                .ConfigureAwait(false);

        await using var stream = await httpResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var thinkFilter = _options.StripThinkTags ? new ThinkTagFilter() : null;
        await foreach (var payload in SseReader.ReadDataAsync(stream, "[DONE]", cancellationToken)
                           .ConfigureAwait(false))
        {
            string? delta = null;
            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException jx)
            {
                // SseReader only yields real `data:` payloads, so a non-JSON
                // frame is a corrupt upstream message, not a keep-alive.
                // Dropping it would complete the answer with a silent HOLE:
                // fail closed and abort the stream instead.
                // FIXED message: vendor frame content is never persisted
                // anywhere (exception text can land in logs), because it can
                // echo arbitrary private request text no regex can recognize.
                throw new System.IO.InvalidDataException(
                    "Chat backend sent a malformed SSE data frame mid-stream; aborting " +
                    "fail-closed rather than silently dropping content.", jx);
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out var errFrame))
                {
                    throw new System.IO.InvalidDataException(
                        "Chat backend sent an error frame mid-stream.");
                }
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String)
                        delta = c.GetString();
                }
                // Any other valid-JSON frame (usage-only final frame, metadata
                // objects, literals) carries no content: skipping it cannot
                // silently lose answer text.
            }
            if (!string.IsNullOrEmpty(delta))
            {
                var emit = thinkFilter is null ? delta : thinkFilter.Push(delta);
                if (!string.IsNullOrEmpty(emit)) yield return new ChatChunk(emit);
            }
        }
        var tail = thinkFilter?.Flush();
        if (!string.IsNullOrEmpty(tail)) yield return new ChatChunk(tail);
        yield return new ChatChunk(string.Empty, IsFinal: true);
    }

    private Task<HttpResponseMessage> PostChatStreamingAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options,
        CancellationToken ct) =>
        PostCoreAsync(history, options, stream: true, ct);

    private async Task<JsonDocument> PostChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options,
        CancellationToken ct)
    {
        using var resp = await PostCoreAsync(history, options, stream: false, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw await ProviderResilience.ToCallExceptionAsync(resp, "Chat backend", ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private Task<HttpResponseMessage> PostCoreAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options,
        bool stream,
        CancellationToken ct)
    {
        if (_options.RequireApiKey && string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                $"Chat API key is missing. Set it via User Secrets / environment variable / Key Vault " +
                $"at configuration key '{_options.ApiKeyConfigKey}'. The key must never be committed to source control.");

        var endpoint = (_options.Endpoint ?? string.Empty).TrimEnd('/');
        var messages = new List<object>(history.Count + 1);
        if (!history.Any(m => m.Role == ChatRole.System) &&
            !string.IsNullOrWhiteSpace(options?.SystemPrompt))
            messages.Add(new { role = "system", content = options!.SystemPrompt! });
        messages.AddRange(history.Select(m =>
            (object)new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }));
        var payload = new
        {
            model = options?.ModelOverride ?? _options.Model,
            messages,
            max_tokens = options?.MaxTokens ?? _options.MaxTokens,
            temperature = options?.Temperature ?? _options.Temperature,
            stream
        };
        var json = JsonSerializer.Serialize(payload, Json);

        Func<HttpRequestMessage> factory = () => new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Headers = { Authorization = string.IsNullOrWhiteSpace(_apiKey)
                ? null : new AuthenticationHeaderValue("Bearer", _apiKey) }
        };
        // Non-success handling is the caller's (ToCallExceptionAsync) so the
        // streaming path never buffers the SSE body here.
        return stream
            ? ProviderResilience.SendStreamingAsync(factory, _http, _resilience, ct)
            : ProviderResilience.SendAsync(factory, _http, _resilience, ct);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
