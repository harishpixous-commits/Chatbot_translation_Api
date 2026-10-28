namespace Pixous.AI.Abstractions;

/// <summary>Result of a translation call.</summary>
public sealed record TranslationResult(
    string TranslatedText,
    string TargetLanguage,
    string? DetectedSourceLanguage,
    string Provider);

/// <summary>Result of a language-detection call.</summary>
public sealed record DetectedLanguage(string Language, double Confidence);

/// <summary>One supported language.</summary>
public sealed record SupportedLanguage(string Code, string Name);

/// <summary>
/// Translation backend seam: Azure AI Translator, Google, LibreTranslate,
/// mocks. New provider = implement + register. No language is hard-coded
/// anywhere in the contract — languages are request/configuration driven.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>Name used in configuration/selection (e.g. "AzureTranslator", "LibreTranslate", "Echo").</summary>
    string ProviderName { get; }

    Task<TranslationResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default);

    Task<DetectedLanguage> DetectAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupportedLanguage>> GetSupportedLanguagesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the effective translation provider. Precedence: per-request → runtime → config.</summary>
public interface ITranslationProviderFactory
{
    ITranslationProvider Get(string? providerName = null);
    IEnumerable<string> RegisteredProviders { get; }
}
