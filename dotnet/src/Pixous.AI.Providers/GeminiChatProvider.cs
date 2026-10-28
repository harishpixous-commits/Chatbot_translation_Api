using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

/// <summary>
/// Google Gemini adapter (<c>POST {endpoint}/models/{model}:generateContent</c>,
/// <c>x-goog-api-key</c> header — key never appears in the URL). Endpoint, model
/// and key come from the user-saved key entry (no vendor lock-in).
/// </summary>
public sealed class GeminiChatProvider : IChatProvider
{
    public string ProviderName => "Gemini";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LlmKeyEntry _key;
    private readonly ResilienceOptions _resilience;

    public GeminiChatProvider(HttpClient http, LlmKeyEntry key, ResilienceOptions resilience)
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
        var text = string.Concat(
            root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? string.Empty));
        var model = root.TryGetProperty("modelVersion", out var m) ? m.GetString() : _key.Model;
        return new ChatResponse(text, ProviderName, model ?? _key.Model);
    }

    /// <summary>
    /// Real incremental streaming via <c>:streamGenerateContent?alt=sse</c>:
    /// each candidate part text is yielded as it arrives; ends with IsFinal.
    /// Same retry/billing discipline: retried only until headers arrive.
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpResponse = await PostCoreAsync(history, options, stream: true, cancellationToken)
            .ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
            throw await ProviderResilience.ToCallExceptionAsync(httpResponse, "Gemini", cancellationToken)
                .ConfigureAwait(false);

        await using var stream = await httpResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var payload in SseReader.ReadDataAsync(stream, terminator: null, cancellationToken)
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
                // SseReader yields only real `data:` payloads, so a non-JSON
                // frame is a corrupt message, not a keep-alive. Skipping it
                // would silently drop answer text: fail closed instead.
                throw new System.IO.InvalidDataException(
                    "Gemini stream sent a malformed data frame mid-stream; aborting " +
                    "fail-closed rather than silently dropping content.", jx);
            }
            using (doc)
            {
                // A mid-stream {"error": ...} frame has no candidates: surface
                // it, never skip silently and end the stream as if complete.
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var errFrame))
                {
                    throw new System.IO.InvalidDataException(
                        "Gemini stream sent an error frame mid-stream.");
                }
                if (doc.RootElement.TryGetProperty("candidates", out var cands) &&
                    cands.GetArrayLength() > 0 &&
                    cands[0].TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    delta = string.Concat(parts.EnumerateArray()
                        .Where(pp => pp.TryGetProperty("text", out _))
                        .Select(pp => pp.GetProperty("text").GetString() ?? string.Empty));
                }
            }
            if (!string.IsNullOrEmpty(delta)) yield return new ChatChunk(delta);
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
            throw await ProviderResilience.ToCallExceptionAsync(resp, "Gemini", ct).ConfigureAwait(false);
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

        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.SystemPrompt) &&
            !history.Any(m => m.Role == ChatRole.System))
            systemParts.Add(options!.SystemPrompt!);

        var contents = new List<object>();
        foreach (var m in history)
        {
            if (m.Role == ChatRole.System) { systemParts.Add(m.Content); continue; }
            contents.Add(new
            {
                role = m.Role == ChatRole.Assistant ? "model" : "user",
                parts = new[] { new { text = m.Content } }
            });
        }
        if (contents.Count == 0)
            contents.Add(new { role = "user", parts = new[] { new { text = "Hello" } } });

        var payload = new Dictionary<string, object>
        {
            ["contents"] = contents
        };
        if (systemParts.Count > 0)
            payload["systemInstruction"] = new { parts = new[] { new { text = string.Join("\n\n", systemParts) } } };

        var gen = new Dictionary<string, object>
        {
            ["maxOutputTokens"] = options?.MaxTokens ?? _key.MaxTokens ?? 512
        };
        var temp = options?.Temperature ?? _key.Temperature;
        if (temp is not null) gen["temperature"] = temp.Value;
        payload["generationConfig"] = gen;

        var json = JsonSerializer.Serialize(payload, Json);
        var endpoint = (_key.Endpoint.Length > 0 ? _key.Endpoint : "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
        var model = options?.ModelOverride ?? _key.Model;
        var path = stream
            ? $"{endpoint}/models/{model}:streamGenerateContent?alt=sse"
            : $"{endpoint}/models/{model}:generateContent";

        Func<HttpRequestMessage> factory = () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-goog-api-key", _key.ApiKey);
            foreach (var h in _key.Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return req;
        };
        return stream
            ? ProviderResilience.SendStreamingAsync(factory, _http, _resilience, ct)
            : ProviderResilience.SendAsync(factory, _http, _resilience, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
