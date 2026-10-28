using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;

namespace Pixous.AI.Providers;

/// <summary>
/// Multi-key, multi-vendor chat chain ("MultiLLM") — the no-vendor-lock-in
/// provider. Tries every enabled, non-locked, non-cooling key in priority order.
///
/// DEFINITIVE rejections (the provider answered "this key/request will not be
/// served") fail over to the next key by default:
///   401 (credentials rejected) → cooled as "invalid"  → NEXT KEY
///   403 (access denied — permission/billing/region)  → cooled as "denied" → NEXT KEY
///   429 (rate/quota signal — NOT proof the call was free) → cooled per Retry-After → NEXT KEY
///   other 4xx (bad model/endpoint for that key)      → cooled → NEXT KEY
///
/// AMBIGUOUS failures (timeout, 5xx, network — the provider may have processed
/// and BILLED the call) do NOT silently fail over: the error surfaces to the
/// caller, unless the operator explicitly sets Resilience:AllowAmbiguousKeyFallback
/// (a deliberate config choice, accepting possible duplicate billing).
///
/// Note: fail-over exists for AVAILABILITY (a revoked/misconfigured/rejected
/// key must not take the chat down) — never for quota evasion: keys sharing a
/// provider account share that account's quota, and adding keys to dodge a
/// provider's limits violates the provider's terms. On account quota
/// exhaustion the correct response is to wait, upgrade, or reduce traffic.
/// A successful call clears the key's failure state. With uploaded knowledge,
/// the top matching passages are injected into the system prompt (grounding).
/// </summary>
public sealed class MultiLlmChatProvider : IChatProvider
{
    public string ProviderName => "MultiLLM";

    private readonly SettingsStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Core.Configuration.ResilienceOptions _resilience;
    private readonly ILogger<MultiLlmChatProvider>? _log;

    public const string HttpClientName = "PixousAiLlm";

    public MultiLlmChatProvider(
        SettingsStore store,
        IHttpClientFactory httpFactory,
        Core.Configuration.ResilienceOptions resilience,
        ILogger<MultiLlmChatProvider>? log = null)
    {
        _store = store;
        _httpFactory = httpFactory;
        _resilience = resilience;
        _log = log;
    }

    public async Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var keys = _store.GetUsableKeys();
        if (keys.Count == 0)
        {
            var configured = _store.GetKeys().Count;
            throw new InvalidOperationException(configured == 0
                ? "No LLM API keys configured. Open the ⚙ Settings panel → LLM APIs and add one " +
                  "(any vendor: OpenAI, Anthropic, Gemini, DeepSeek, Groq, Ollama, custom…), " +
                  "or switch the chat engine to KnowledgeBase."
                : $"All {configured} configured LLM keys are disabled or cooling down after errors. " +
                  "Retry shortly, or check ⚙ Settings → LLM APIs.");
        }

        var failures = new List<string>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var adapter = LlmVendor.Create(
                    _httpFactory.CreateClient(HttpClientName), key, _resilience);
                var (hist, prompt) = WithKnowledge(history, options?.SystemPrompt);
                var callOptions = options is null
                    ? new ChatRequestOptions(SystemPrompt: prompt)
                    : new ChatRequestOptions(prompt, options.ModelOverride, options.MaxTokens,
                        options.Temperature, options.ResponseLanguage);

                var reply = await adapter.ChatAsync(hist, callOptions, cancellationToken).ConfigureAwait(false);
                _store.MarkSuccess(key.Id);
                _log?.LogInformation("MultiLLM ok. Key={KeyName} Vendor={Vendor} Model={Model}",
                    key.Name, key.Vendor, reply.Model ?? key.Model);
                return new ChatResponse(reply.Content, ProviderName, reply.Model ?? key.Model);
            }
            catch (LlmCallException ex)
            {
                _store.MarkFailure(key.Id, ex.StatusNumber, ex.Message, ex.RetryAfterSeconds);
                failures.Add($"{key.Name}: HTTP {ex.StatusNumber ?? 0}");
                // 408/5xx/unknown: AMBIGUOUS — possibly processed+billed — stop unless opted in.
                if (ex.StatusNumber is null or 408 or >= 500 && !_resilience.AllowAmbiguousKeyFallback)
                    throw new HttpRequestException(AmbiguousMessage(key.Name,
                        ex.StatusNumber is null ? "unknown status" : $"HTTP {ex.StatusNumber}"));
                // 429: quota / rate limit. Honored, not evaded — spending a
                // DIFFERENT key's quota is not availability. Key is cooling;
                // surface the vendor's answer (keeps Retry-After) unless the
                // operator explicitly opted into multi-key quota pooling.
                if (ex.StatusNumber == 429 && !_resilience.AllowQuotaKeyFallback)
                    throw;
                // Other 4xx (401 invalid key, 403 denied, 400/404 config): the
                // provider definitively rejected the call, which normally means
                // no completion was produced — but a 4xx does not PROVE the
                // call was free (check the provider dashboard when in doubt).
                _log?.LogWarning("MultiLLM key rejected. Key={KeyName} Status={Status} — trying next.",
                    key.Name, ex.StatusNumber);
            }
            catch (HttpRequestException ex)
            {
                // Network/timeout — AMBIGUOUS: the provider may have processed
                // and billed the prompt. Do not re-send it to another key unless
                // the operator opted in.
                if (!_resilience.AllowAmbiguousKeyFallback)
                    throw new HttpRequestException(AmbiguousMessage(key.Name, "network/timeout"));
                failures.Add($"{key.Name}: {Truncate(ex.Message, 80)}");
                _log?.LogWarning("MultiLLM key unreachable. Key={KeyName} — trying next.", key.Name);
            }
            catch (InvalidOperationException ex)
            {
                // Configuration problem for THIS key (e.g. missing key text) — cool it, try next.
                _store.MarkFailure(key.Id, 400, ex.Message);
                failures.Add($"{key.Name}: {Truncate(ex.Message, 80)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{key.Name}: {Truncate(ex.Message, 80)}");
                // Logs are a persistence surface: category + type only, never
                // ex.Message (provider text can echo private request content).
                _log?.LogWarning("MultiLLM key errored. Key={KeyName} Type={Type} — trying next.",
                    key.Name, ex.GetType().Name);
            }
        }

        // Nothing worked: 502 (provider upstream) with a secret-free summary.
        throw new HttpRequestException(
            "All configured LLM keys failed: " + string.Join("; ", failures) + ".");
    }

    /// <summary>
    /// Incremental streaming with the same failover discipline as ChatAsync,
    /// with one hard rule: fail-over can only happen BEFORE the first chunk is
    /// emitted. Once output has been shown to the caller, switching keys would
    /// duplicate/garble the visible reply (and double-bill), so mid-stream
    /// failures always surface. A key is marked successful only after its
    /// stream completes with IsFinal.
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var keys = _store.GetUsableKeys();
        if (keys.Count == 0)
        {
            var configured = _store.GetKeys().Count;
            throw new InvalidOperationException(configured == 0
                ? "No LLM API keys configured. Open the ⚙ Settings panel → LLM APIs and add one, " +
                  "or switch the chat engine to KnowledgeBase."
                : $"All {configured} configured LLM keys are disabled, locked or cooling down. " +
                  "Retry shortly, or check ⚙ Settings → LLM APIs.");
        }

        var failures = new List<string>();
        var keyIndex = -1;
        while (++keyIndex < keys.Count)
        {
            var key = keys[keyIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var adapter = LlmVendor.Create(_httpFactory.CreateClient(HttpClientName), key, _resilience);
            var (hist, prompt) = WithKnowledge(history, options?.SystemPrompt);
            var callOptions = options is null
                ? new ChatRequestOptions(SystemPrompt: prompt)
                : new ChatRequestOptions(prompt, options.ModelOverride, options.MaxTokens,
                    options.Temperature, options.ResponseLanguage);

            var emitted = false;     // any content chunk already shown to the caller?
            var completed = false;   // stream reached IsFinal?
            Exception? preFirstFailure = null;
            await using var iter = adapter.StreamChatAsync(hist, callOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                ChatChunk chunk;
                try
                {
                    if (!await iter.MoveNextAsync().ConfigureAwait(false)) { completed = true; break; }
                    chunk = iter.Current;
                }
                catch (LlmCallException ex) when (!emitted)
                {
                    // Definitive rejection before any output: mark + next key.
                    // Ambiguous (408/5xx/unknown): stop unless opted in.
                    _store.MarkFailure(key.Id, ex.StatusNumber, ex.Message, ex.RetryAfterSeconds);
                    failures.Add($"{key.Name}: HTTP {ex.StatusNumber ?? 0}");
                    if (ex.StatusNumber is null or 408 or >= 500 && !_resilience.AllowAmbiguousKeyFallback)
                        throw new HttpRequestException(AmbiguousMessage(key.Name,
                            ex.StatusNumber is null ? "unknown status" : $"HTTP {ex.StatusNumber}"));
                    // 429 = quota/rate answer: honored, not evaded (see non-streaming path).
                    if (ex.StatusNumber == 429 && !_resilience.AllowQuotaKeyFallback)
                        throw;
                    preFirstFailure = ex;
                    break;
                }
                catch (HttpRequestException) when (!emitted)
                {
                    if (!_resilience.AllowAmbiguousKeyFallback)
                        throw new HttpRequestException(AmbiguousMessage(key.Name, "network/timeout"));
                    failures.Add($"{key.Name}: network/timeout");
                    break;
                }
                catch (InvalidOperationException ex) when (!emitted)
                {
                    _store.MarkFailure(key.Id, 400, ex.Message);
                    failures.Add($"{key.Name}: {Truncate(ex.Message, 80)}");
                    break;
                }
                // Past the first chunk: no catch — failures surface to the caller.
                if (chunk.IsFinal) { completed = true; break; }
                emitted = true;
                yield return chunk;
            }

            if (completed)
            {
                _store.MarkSuccess(key.Id);
                _log?.LogInformation("MultiLLM stream ok. Key={KeyName} Vendor={Vendor}", key.Name, key.Vendor);
                yield break;
            }
            if (preFirstFailure is not null)
                _log?.LogWarning("MultiLLM stream key rejected pre-output. Key={KeyName} — trying next.", key.Name);
        }

        throw new HttpRequestException(
            "All configured LLM keys failed: " + string.Join("; ", failures) + ".");
    }

    /// <summary>
    /// Prepends top matching knowledge (uploaded docs + FAQ) as a grounded
    /// context block on the system message, so answers cite configured
    /// knowledge instead of guessing.
    /// </summary>
    internal (IReadOnlyList<ChatMessage> History, string? Prompt) WithKnowledge(
        IReadOnlyList<ChatMessage> history, string? systemPrompt)
    {
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User)?.Content;
        var hits = string.IsNullOrWhiteSpace(lastUser)
            ? Array.Empty<KnowledgeHit>()
            : _store.Search(lastUser, topN: 3, minScore: 2);
        if (hits.Count == 0) return (history, systemPrompt);

        var context = new StringBuilder();
        context.Append("Grounded knowledge (from the configured knowledge base / uploaded documents — ")
               .Append("use it when relevant, never invent beyond it):\n");
        foreach (var hit in hits)
        {
            context.Append("\n--- [").Append(hit.Source).Append("]\n");
            var text = hit.Text.Length > 700 ? hit.Text[..700] + "…" : hit.Text;
            context.Append(text);
        }

        var list = history.ToList();
        var sysIdx = list.FindIndex(m => m.Role == ChatRole.System);
        if (sysIdx >= 0)
        {
            list[sysIdx] = new ChatMessage(ChatRole.System,
                list[sysIdx].Content + "\n\n" + context);
        }
        else
        {
            var prompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? context.ToString()
                : systemPrompt + "\n\n" + context;
            list.Insert(0, new ChatMessage(ChatRole.System, prompt));
            systemPrompt = null; // already embedded — adapters must not add it twice
        }
        return (list, systemPrompt);
    }

    private static string AmbiguousMessage(string keyName, string why) =>
        $"LLM key '{keyName}' failed with an ambiguous error ({why}): the provider may still " +
        "have processed — and billed — the request, so the prompt was NOT re-sent to another key. " +
        "Retry the request yourself if the provider confirms it did not run, or set " +
        "Resilience:AllowAmbiguousKeyFallback=true to let the chain continue automatically " +
        "(accepting possible duplicate charges).";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
