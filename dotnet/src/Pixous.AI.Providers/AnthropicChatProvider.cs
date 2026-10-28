using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

/// <summary>
/// Anthropic Messages API adapter (<c>POST {endpoint}/v1/messages</c>,
/// <c>x-api-key</c> header). Vendor-lock-in-free: the endpoint/model/key all
/// come from the user-saved key entry.
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider
{
    public string ProviderName => "Anthropic";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LlmKeyEntry _key;
    private readonly ResilienceOptions _resilience;

    public AnthropicChatProvider(HttpClient http, LlmKeyEntry key, ResilienceOptions resilience)
    {
        _http = http;
        _key = key;
        _resilience = resilience;
    }

    public async Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(history, options, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var text = string.Concat(root.GetProperty("content").EnumerateArray()
            .Where(b => b.TryGetProperty("text", out _))
            .Select(b => b.GetProperty("text").GetString() ?? string.Empty));
        var model = root.TryGetProperty("model", out var m) ? m.GetString() : _key.Model;
        return new ChatResponse(text, ProviderName, model);
    }

    /// <summary>
    /// Real incremental SSE streaming (<c>"stream": true</c>): yields each
    /// content_block_delta text as it arrives; ends with IsFinal. Same
    /// retry/billing discipline as OpenAI-compatible: retried only until
    /// headers arrive, never mid-stream.
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpResponse = await PostCoreAsync(history, options, stream: true, cancellationToken)
            .ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
            throw await ProviderResilience.ToCallExceptionAsync(httpResponse, "Anthropic", cancellationToken)
                .ConfigureAwait(false);

        await using var stream = await httpResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var payload in SseReader.ReadDataAsync(stream, terminator: null, cancellationToken)
                           .ConfigureAwait(false))
        {
            string? delta = null;
            var done = false;
            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException jx)
            {
                // SseReader yields only real `data:` payloads; Anthropic pings
                // arrive as valid {"type":"ping"} JSON, so a non-JSON frame is
                // a corrupt message. Skipping it would silently drop answer
                // text: fail closed instead.
                throw new System.IO.InvalidDataException(
                    "Anthropic stream sent a malformed data frame mid-stream; aborting " +
                    "fail-closed rather than silently dropping content.", jx);
            }
            using (doc)
            {
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "content_block_delta" &&
                    doc.RootElement.TryGetProperty("delta", out var d) &&
                    d.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    delta = txt.GetString();
                else if (type == "message_stop")
                    done = true;
                else if (type == "error")
                    // In-stream error event, not a connection failure: map it
                    // like the other adapters so endpoints label it
                    // UPSTREAM_ABORTED, not UPSTREAM_UNREACHABLE.
                    throw new System.IO.InvalidDataException("Anthropic stream error event mid-stream.");
            }
            if (!string.IsNullOrEmpty(delta)) yield return new ChatChunk(delta);
            if (done) break;
        }
        yield return new ChatChunk(string.Empty, IsFinal: true);
    }

    private async Task<JsonDocument> PostAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options,
        CancellationToken ct)
    {
        using var resp = await PostCoreAsync(history, options, stream: false, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw await ProviderResilience.ToCallExceptionAsync(resp, "Anthropic", ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private Task<HttpResponseMessage> PostCoreAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options,
        bool stream,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_key.ApiKey))
            throw new InvalidOperationException(
                $"API key missing for key entry '{_key.Name}'. Add it in ⚙ Settings → LLM APIs.");

        // System messages (history + configured prompt) merge into Anthropic's "system" field.
        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.SystemPrompt) &&
            !history.Any(m => m.Role == ChatRole.System))
            systemParts.Add(options!.SystemPrompt!);

        var messages = new List<object>();
        foreach (var m in history)
        {
            if (m.Role == ChatRole.System)
            {
                systemParts.Add(m.Content);
                continue;
            }
            var role = m.Role == ChatRole.Assistant ? "assistant" : "user";
            // Anthropic requires alternating roles — merge consecutive same-role messages.
            if (messages.Count > 0 &&
                messages[^1] is Dictionary<string, object> existing &&
                (string)existing["role"] == role)
            {
                existing["content"] = (string)existing["content"] + "\n" + m.Content;
                continue;
            }
            messages.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = m.Content });
        }
        if (messages.Count == 0)
            messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = "Hello" });
        else if (messages[0] is Dictionary<string, object> first && (string)first["role"] == "assistant")
            messages.RemoveAt(0); // must start with a user turn

        var payload = new Dictionary<string, object>
        {
            ["model"] = options?.ModelOverride ?? _key.Model,
            ["max_tokens"] = options?.MaxTokens ?? _key.MaxTokens ?? 512,
            ["messages"] = messages,
            ["stream"] = stream
        };
        if (systemParts.Count > 0) payload["system"] = string.Join("\n\n", systemParts);
        var temp = options?.Temperature ?? _key.Temperature;
        if (temp is not null) payload["temperature"] = temp.Value;

        var json = JsonSerializer.Serialize(payload, Json);
        var endpoint = (_key.Endpoint.Length > 0 ? _key.Endpoint : "https://api.anthropic.com").TrimEnd('/');

        Func<HttpRequestMessage> factory = () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-api-key", _key.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            foreach (var h in _key.Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return req;
        };
        return stream
            ? ProviderResilience.SendStreamingAsync(factory, _http, _resilience, ct)
            : ProviderResilience.SendAsync(factory, _http, _resilience, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
