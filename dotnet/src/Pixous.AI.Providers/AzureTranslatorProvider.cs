using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

/// <summary>
/// Azure AI Translator (REST, api-version 3.0). Key from secure config —
/// never hard-coded, never logged. Transient failures retried with backoff.
/// </summary>
public sealed class AzureTranslatorProvider : ITranslationProvider
{
    public string ProviderName => "AzureTranslator";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AzureTranslatorOptions _options;
    private readonly ResilienceOptions _resilience;
    private readonly string? _apiKey;

    public AzureTranslatorProvider(
        HttpClient http, AzureTranslatorOptions options, ResilienceOptions resilience, string? apiKey)
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
        EnsureKey();

        var endpoint = (_options.Endpoint ?? string.Empty).TrimEnd('/');
        var url = $"{endpoint}/translate?api-version=3.0&to={Uri.EscapeDataString(targetLanguage)}";
        if (!string.IsNullOrWhiteSpace(sourceLanguage))
            url += $"&from={Uri.EscapeDataString(sourceLanguage)}";
        var json = JsonSerializer.Serialize(new[] { new { Text = text } }, Json);

        using var response = await ProviderResilience.SendAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey!);
                if (!string.IsNullOrWhiteSpace(_options.Region))
                    req.Headers.Add("Ocp-Apim-Subscription-Region", _options.Region);
                return req;
            },
            _http, _resilience, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Translator backend returned {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement[0];
        var translated = first.GetProperty("translations")[0].GetProperty("text").GetString() ?? string.Empty;
        string? detected = null;
        if (first.TryGetProperty("detectedLanguage", out var det) &&
            det.TryGetProperty("language", out var lang))
            detected = lang.GetString();

        return new TranslationResult(translated, targetLanguage, detected ?? sourceLanguage, ProviderName);
    }

    public async Task<DetectedLanguage> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var r = await TranslateAsync(text, "en", null, cancellationToken).ConfigureAwait(false);
        return new DetectedLanguage(r.DetectedSourceLanguage ?? "und", r.DetectedSourceLanguage is null ? 0 : 1);
    }

    public async Task<IReadOnlyList<SupportedLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken = default)
    {
        EnsureKey();
        var endpoint = (_options.Endpoint ?? string.Empty).TrimEnd('/');
        using var response = await ProviderResilience.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/languages?api-version=3.0&scope=translation"),
            _http, _resilience, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var list = new List<SupportedLanguage>();
        if (doc.RootElement.TryGetProperty("translation", out var t))
            foreach (var prop in t.EnumerateObject())
                list.Add(new SupportedLanguage(prop.Name, prop.Value.GetProperty("name").GetString() ?? prop.Name));
        return list;
    }

    private void EnsureKey()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                $"Translator API key is missing. Set it via User Secrets / environment variable / Key Vault " +
                $"at configuration key '{_options.ApiKeyConfigKey}'.");
    }
}
