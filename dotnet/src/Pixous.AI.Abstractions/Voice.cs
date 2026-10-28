namespace Pixous.AI.Abstractions;

/// <summary>Transcribed speech.</summary>
public sealed record SttResult(string Text, string Provider);

/// <summary>Synthesized speech audio.</summary>
public sealed record TtsResult(byte[] Audio, string ContentType, string Provider);

/// <summary>
/// Speech-to-text backend (Whisper-compatible endpoints, cloud STT, mocks).
/// Selected at runtime from the Settings voice configuration.
/// </summary>
public interface ISpeechToTextProvider
{
    string ProviderName { get; }

    Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? language = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Text-to-speech backend.</summary>
public interface ITextToSpeechProvider
{
    string ProviderName { get; }

    Task<TtsResult> SynthesizeAsync(
        string text,
        string? voice = null,
        CancellationToken cancellationToken = default);
}
