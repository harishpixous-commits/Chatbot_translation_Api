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

    public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        var source = string.IsNullOrWhiteSpace(sourceLanguage)
            ? (await DetectAsync(text, cancellationToken).ConfigureAwait(false)).Language
            : sourceLanguage;
        if (source == "und")
            throw new InvalidOperationException(
                "PixousSelfHosted: source language could not be detected (script-level heuristic). Pass sourceLanguage explicitly.");
        if (string.Equals(source, targetLanguage, StringComparison.OrdinalIgnoreCase))
            return new TranslationResult(text, targetLanguage, source, ProviderName);

        var payload = JsonSerializer.Serialize(
            new { source, target = targetLanguage, items = new[] { text } }, Json);

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
            throw new HttpRequestException($"Pixous engine sidecar returned {(int)response.StatusCode}: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var translations = doc.RootElement.GetProperty("translations");
        var translated = translations.GetArrayLength() > 0
            ? translations[0].GetString() ?? string.Empty
            : string.Empty;
        return new TranslationResult(translated, targetLanguage, source, ProviderName);
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

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
