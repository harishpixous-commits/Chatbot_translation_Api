namespace Pixous.AI.Abstractions;

/// <summary>Result of a translation call.</summary>
public sealed record TranslationResult(
    string TranslatedText,
    string TargetLanguage,
    string? DetectedSourceLanguage,
    string Provider,
    bool? ScriptAnomaly = null);
// ScriptAnomaly: null = provider did not check; true = output contains letters from a script that is neither
// the target's, Latin, nor present in the source (objective script check only, NOT a translation-accuracy measure).

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

/// <summary>
/// The translation engine refused the request for capacity reasons, not because the input
/// is invalid or the service is down: <see cref="Busy"/> (queue full, retry shortly) or a
/// resource limit (time/memory guard tripped for this input). The message is FIXED wording;
/// no upstream body is ever carried.
/// </summary>
public sealed class TranslationCapacityException : System.Net.Http.HttpRequestException
{
    public bool Busy { get; }
    public int? RetryAfterSeconds { get; }
    public TranslationCapacityException(bool busy, int? retryAfterSeconds)
        : base(busy ? "The translation engine is busy." : "The translation engine could not process this input within its resource limits.")
    { Busy = busy; RetryAfterSeconds = retryAfterSeconds; }
}
