using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

/// <summary>
/// LibreTranslate (self-hosted or public instance). Usually keyless;
/// optional key via config. Transient failures retried with backoff.
/// </summary>
public sealed class LibreTranslateProvider : ITranslationProvider
{
    public string ProviderName => "LibreTranslate";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LibreTranslateOptions _options;
    private readonly ResilienceOptions _resilience;
    private readonly string? _apiKey;

    public LibreTranslateProvider(
        HttpClient http, LibreTranslateOptions options, ResilienceOptions resilience, string? apiKey)
    {
        _http = http;
        _options = options;
        _resilience = resilience;
        _apiKey = apiKey;
    }

    public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        var endpoint = Endpoint();

        var payload = new Dictionary<string, string>
        {
            ["q"] = text,
            ["source"] = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage,
            ["target"] = targetLanguage,
            ["format"] = "text"
        };
        if (!string.IsNullOrWhiteSpace(_apiKey)) payload["api_key"] = _apiKey;
        var json = JsonSerializer.Serialize(payload, Json);

        using var response = await ProviderResilience.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/translate")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            },
            _http, _resilience, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"LibreTranslate backend returned {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        var translated = doc.RootElement.TryGetProperty("translatedText", out var t)
            ? t.GetString() ?? string.Empty : string.Empty;
        string? detected = null;
        if (doc.RootElement.TryGetProperty("detectedLanguage", out var d))
            detected = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("language", out var l)
                ? l.GetString() : d.GetString();
        return new TranslationResult(translated, targetLanguage, detected ?? sourceLanguage, ProviderName);
    }

    public async Task<DetectedLanguage> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var endpoint = Endpoint();
        var json = JsonSerializer.Serialize(new { q = text }, Json);

        using var response = await ProviderResilience.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/detect")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            },
            _http, _resilience, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Undefined
            ? new DetectedLanguage("und", 0)
            : new DetectedLanguage(
                first.GetProperty("language").GetString() ?? "und",
                first.TryGetProperty("confidence", out var c) ? c.GetDouble() / 100.0 : 0);
    }

    public async Task<IReadOnlyList<SupportedLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint();
        using var response = await ProviderResilience.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/languages"),
            _http, _resilience, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var list = new List<SupportedLanguage>();
        foreach (var el in doc.RootElement.EnumerateArray())
            list.Add(new SupportedLanguage(
                el.GetProperty("code").GetString() ?? "?",
                el.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?"));
        return list;
    }

    private string Endpoint()
    {
        var endpoint = (_options.Endpoint ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("LibreTranslate:Endpoint must be configured.");
        return endpoint;
    }
}
