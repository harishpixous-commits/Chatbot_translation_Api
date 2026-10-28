using Pixous.AI.Abstractions;

namespace Pixous.AI.Providers;

/// <summary>Keyless translation provider for development, demos and tests.</summary>
public sealed class EchoTranslationProvider : ITranslationProvider
{
    public string ProviderName => "Echo";

    public Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        return Task.FromResult(new TranslationResult(
            TranslatedText: $"[{targetLanguage}] {text}",
            TargetLanguage: targetLanguage,
            DetectedSourceLanguage: sourceLanguage,
            Provider: ProviderName));
    }

    public Task<DetectedLanguage> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Task.FromResult(new DetectedLanguage("und", 0));
    }

    public Task<IReadOnlyList<SupportedLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SupportedLanguage> langs = new[]
        {
            new SupportedLanguage("en", "English"),
            new SupportedLanguage("ta", "Tamil"),
            new SupportedLanguage("hi", "Hindi"),
            new SupportedLanguage("es", "Spanish"),
            new SupportedLanguage("fr", "French"),
        };
        return Task.FromResult(langs);
    }
}
