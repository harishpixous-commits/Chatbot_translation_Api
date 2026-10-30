using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

/// <summary>
/// Pixous self-hosted translation engine sidecar (IndicTrans2 CT2, CPU).
/// No external provider is contacted: translation runs on the operator's own
/// host via the sidecar's <c>POST /translate</c>. Optional bearer token via
/// config pointer; the sidecar's localhost-only default is not auth — see the
/// engine-sidecar README before exposing it on a network.
/// Indic-to-Indic pairs pivot through English inside the sidecar
/// (<c>pivot: true</c> in its response); pivot quality is lower than direct
/// pairs and is surfaced in documentation, not hidden.
/// Detection is script-level heuristic only (see <see cref="DetectAsync"/>).
/// </summary>
public sealed class PixousSelfHostedTranslationProvider : ITranslationProvider
{
    public string ProviderName => "PixousSelfHosted";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly PixousEngineOptions _options;
    private readonly ResilienceOptions _resilience;
    private readonly string? _token;

    public PixousSelfHostedTranslationProvider(
        HttpClient http, PixousEngineOptions options, ResilienceOptions resilience, string? token)
    {
        _http = http;
        _options = options;
        _resilience = resilience;
        _token = token;
    }

    private (int Item, int Items, int Total)? _limits; // cached sidecar /health limits (success only)

    /// <summary>
    /// When a text is split, read the sidecar's own /health limits (bounded 5 s, success cached) and fail with a
    /// precise configuration error when MaxItemChars or the piece count/total exceeds them. An older sidecar without
    /// <c>limits</c>, or an unreachable /health, is not a failure here: the 413 handling below still reports it.
    /// </summary>
    private async Task EnsureLimitsCompatibleAsync(List<string> pieces, CancellationToken ct)
    {
        var lim = _limits;
        if (lim is null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var resp = await _http.GetAsync($"{Endpoint()}/health", cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("limits", out var l) || l.ValueKind != JsonValueKind.Object) return;
                int Get(string n) => l.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : int.MaxValue;
                lim = (Get("max_item_chars"), Get("max_items"), Get("max_total_chars"));
                _limits = lim;
            }
            catch (Exception ex) when (ct.IsCancellationRequested ? false : ex is HttpRequestException or JsonException or OperationCanceledException) { return; }
        }
        var (maxItem, maxItems, maxTotal) = lim.Value;
        if (_options.MaxItemChars > maxItem)
            throw new InvalidOperationException(
                $"PixousSelfHosted configuration mismatch: PixousEngine:MaxItemChars ({_options.MaxItemChars}) is above the sidecar PIXOUS_MAX_ITEM_CHARS ({maxItem}).");
        if (pieces.Count > maxItems)
            throw new InvalidOperationException(
                $"PixousSelfHosted configuration mismatch: a split text needs {pieces.Count} items, above the sidecar PIXOUS_MAX_ITEMS ({maxItems}).");
        if (pieces.Sum(p => p.Length) > maxTotal)
            throw new InvalidOperationException(
                $"PixousSelfHosted configuration mismatch: a split text is above the sidecar PIXOUS_MAX_TOTAL_CHARS ({maxTotal}).");
    }

    public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        cancellationToken.ThrowIfCancellationRequested();
        var source = string.IsNullOrWhiteSpace(sourceLanguage)
            ? (await DetectAsync(text, cancellationToken).ConfigureAwait(false)).Language
            : sourceLanguage;
        if (source == "und")
            throw new InvalidOperationException(
                "PixousSelfHosted: source language could not be detected (script-level heuristic). Pass sourceLanguage explicitly.");
        if (string.Equals(source, targetLanguage, StringComparison.OrdinalIgnoreCase))
            return new TranslationResult(text, targetLanguage, source, ProviderName);

        var pieces = _options.MaxItemChars >= 2 && text.Length > _options.MaxItemChars
            ? TranslationTextSplitter.Split(text, _options.MaxItemChars)
            : new List<string> { text };
        if (pieces.Count > 1)
            await EnsureLimitsCompatibleAsync(pieces, cancellationToken).ConfigureAwait(false);
        // Trailing whitespace of each piece is kept locally and re-attached, so the engine never has to
        // preserve it. A piece that is only whitespace is not sent.
        var trailing = pieces.Select(p => p[p.TrimEnd().Length..]).ToList();
        var sendItems = pieces.Select(p => p.TrimEnd()).ToList();
        if (pieces.Count == 1) { sendItems[0] = text; trailing[0] = string.Empty; }
        var payload = JsonSerializer.Serialize(
            new { source, target = targetLanguage, items = sendItems }, Json);

        using var response = await ProviderResilience.SendAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint()}/translate")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(_token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                return req;
            },
            _http, _resilience, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The sidecar answers 413 when an item, the item count or the total size exceeds
            // its own limits (PIXOUS_MAX_ITEM_CHARS default 2000 etc.). That is caller input,
            // not an outage: surface it as a validation error (HTTP 400) instead of the generic
            // "temporarily unavailable" 502. The public message is FIXED wording: the upstream
            // body is never forwarded (it could reflect private input, internal details, HTML or
            // oversized content). The inner exception keeps only the status code for server-side
            // diagnostics; the response already carries the traceId for correlation.
            // 400 (unsupported pair after the host allow-list = configuration mismatch),
            // 401/403, 422 and 5xx stay upstream errors.
            var code = (int)response.StatusCode;
            // A 413 for a text THIS provider split into pieces (each <= MaxItemChars) cannot be caller input
            // size: the configured MaxItemChars is above the sidecar's own limit (or the item/total
            // limits are lower than the split needs). That is a server configuration error, reported
            // precisely in the server-side exception and as the generic upstream failure to the caller.
            if (code is 413 && pieces.Count > 1)
                throw new InvalidOperationException(
                    "PixousSelfHosted configuration mismatch: the sidecar rejected pieces of a split text with 413. " +
                    "Set PixousEngine:MaxItemChars <= the sidecar PIXOUS_MAX_ITEM_CHARS, and make sure " +
                    "PIXOUS_MAX_ITEMS / PIXOUS_MAX_TOTAL_CHARS allow the pieces.");
            if (code is 413)
                throw new ArgumentException(SizeLimitMessage,
                    new HttpRequestException($"Pixous engine sidecar returned {code} (size limit)."));
            // Capacity refusals from the sidecar's own guards are typed (fixed message, no body).
            // The sidecar's detail text is NEVER put in the exception or logs: error bodies can
            // reflect request input. Only the status code is kept for diagnostics.
            if (code is 429)
                throw new TranslationCapacityException(true,
                    (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5);
            if (code is 503 && body.Contains("\"resource_limit\"", StringComparison.Ordinal))
                throw new TranslationCapacityException(false, null);
            throw new HttpRequestException($"Pixous engine sidecar returned {code}.");
        }

        using var doc = JsonDocument.Parse(body);
        var translations = doc.RootElement.GetProperty("translations");
        string translated;
        if (pieces.Count == 1)
        {
            translated = translations.GetArrayLength() > 0 ? translations[0].GetString() ?? string.Empty : string.Empty;
        }
        else
        {
            if (translations.GetArrayLength() != pieces.Count)
                throw new HttpRequestException("Pixous engine sidecar returned an unexpected item count.");
            var sb = new StringBuilder();
            for (var i = 0; i < pieces.Count; i++)
                sb.Append(translations[i].GetString() ?? string.Empty).Append(trailing[i]);
            translated = sb.ToString();
        }
        bool? anomaly = null;
        if (doc.RootElement.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object
            && q.TryGetProperty("checked", out var chk) && chk.ValueKind == JsonValueKind.True
            && q.TryGetProperty("script_anomaly_items", out var items) && items.ValueKind == JsonValueKind.Array)
            anomaly = items.GetArrayLength() > 0;
        return new TranslationResult(translated, targetLanguage, source, ProviderName, anomaly);
    }

    /// <summary>
    /// Script-level heuristic detection across the six priority languages +
    /// English. It counts characters in each language's Unicode script block
    /// and returns the majority script. It CANNOT distinguish languages that
    /// share a script (e.g. Hindi vs Marathi), returns "und" for empty or
    /// script-less input, and is not a general language identifier — callers
    /// needing reliable detection should pass the source language explicitly.
    /// </summary>
    public Task<DetectedLanguage> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var counts = new Dictionary<string, int>();
        var total = 0;
        foreach (var ch in text)
        {
            var code = (int)ch;
            string? lang = code switch
            {
                >= 0x0B80 and <= 0x0BFF => "ta",
                >= 0x0900 and <= 0x097F => "hi",
                >= 0x0C00 and <= 0x0C7F => "te",
                >= 0x0D00 and <= 0x0D7F => "ml",
                >= 0x0C80 and <= 0x0CFF => "kn",
                >= 0x0041 and <= 0x007A => "en",
                _ => null
            };
            if (lang is null) continue;
            counts[lang] = counts.GetValueOrDefault(lang) + 1;
            total++;
        }
        if (total == 0)
            return Task.FromResult(new DetectedLanguage("und", 0));
        var best = counts.MaxBy(kv => kv.Value);
        return Task.FromResult(new DetectedLanguage(best.Key, (double)best.Value / total));
    }

    public Task<IReadOnlyList<SupportedLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken = default)
    {
        // The sidecar serves the 22 scheduled Indic languages + English that
        // IndicTrans2 covers; the six priority languages are named, the rest
        // are exposed by code. Static list: the sidecar's /languages endpoint
        // carries the same data, and a static list keeps this call offline-safe.
        IReadOnlyList<SupportedLanguage> list = new[]
        {
            new SupportedLanguage("en", "English"), new SupportedLanguage("ta", "Tamil"),
            new SupportedLanguage("hi", "Hindi"), new SupportedLanguage("te", "Telugu"),
            new SupportedLanguage("ml", "Malayalam"), new SupportedLanguage("kn", "Kannada"),
            new SupportedLanguage("as", "Assamese"), new SupportedLanguage("bn", "Bengali"),
            new SupportedLanguage("brx", "Bodo"), new SupportedLanguage("doi", "Dogri"),
            new SupportedLanguage("gu", "Gujarati"), new SupportedLanguage("gom", "Konkani"),
            new SupportedLanguage("ks", "Kashmiri"), new SupportedLanguage("mai", "Maithili"),
            new SupportedLanguage("mr", "Marathi"), new SupportedLanguage("mni", "Manipuri"),
            new SupportedLanguage("ne", "Nepali"), new SupportedLanguage("or", "Odia"),
            new SupportedLanguage("pa", "Punjabi"), new SupportedLanguage("sa", "Sanskrit"),
            new SupportedLanguage("sat", "Santali"), new SupportedLanguage("sd", "Sindhi"),
            new SupportedLanguage("ur", "Urdu"),
        };
        return Task.FromResult(list);
    }

    private string Endpoint()
    {
        var endpoint = (_options.Endpoint ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("AiSuite:PixousEngine:Endpoint must be configured (e.g. http://127.0.0.1:8765).");
        return endpoint;
    }

    public const string SizeLimitMessage =
        "The text is larger than the translation engine accepts. Shorten it or split it into smaller parts.";

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
