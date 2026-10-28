using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers.Resilience;

namespace Pixous.AI.Providers;

// ── Echo (keyless demo) ──────────────────────────────────────────────────
public sealed class EchoSpeechToTextProvider : ISpeechToTextProvider
{
    public string ProviderName => VoiceSettings.Echo;

    public async Task<SttResult> TranscribeAsync(Stream audio, string fileName, string? language = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await audio.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return new SttResult(
            $"(demo speech-to-text) Received {ms.Length} bytes of audio. " +
            "Configure a real Speech-to-Text API in ⚙ Settings → Voice.",
            ProviderName);
    }
}

public sealed class EchoTextToSpeechProvider : ITextToSpeechProvider
{
    public string ProviderName => VoiceSettings.Echo;

    public Task<TtsResult> SynthesizeAsync(string text, string? voice = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TtsResult(BeepWav(), "audio/wav", ProviderName));

    /// <summary>A short, quiet 660 Hz sine beep so the demo audibly responds.</summary>
    internal static byte[] BeepWav()
    {
        const int sampleRate = 22050;
        const double seconds = 0.25;
        const double freq = 660;
        var samples = (int)(sampleRate * seconds);
        var data = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var env = Math.Min(1.0, i / 500.0) * Math.Min(1.0, (samples - i) / 500.0);
            var v = (short)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * 6000 * env);
            data[i * 2] = (byte)(v & 0xff);
            data[i * 2 + 1] = (byte)(v >> 8);
        }
        using var ms = new MemoryStream();
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void U32(uint v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 24)); }
        void U16(ushort v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }

        Ascii("RIFF"); U32((uint)(36 + data.Length)); Ascii("WAVE");
        Ascii("fmt "); U32(16); U16(1); U16(1); U32(sampleRate); U32(sampleRate * 2); U16(2); U16(16);
        Ascii("data"); U32((uint)data.Length);
        ms.Write(data);
        return ms.ToArray();
    }
}

// ── OpenAI-compatible (Whisper / OpenAI TTS and every compatible clone) ──
public sealed class OpenAiCompatibleSttProvider : ISpeechToTextProvider
{
    public string ProviderName => VoiceSettings.OpenAiCompatible;

    private readonly HttpClient _http;
    private readonly VoiceSettings _cfg;
    private readonly ResilienceOptions _resilience;

    public OpenAiCompatibleSttProvider(HttpClient http, VoiceSettings cfg, ResilienceOptions resilience)
    {
        _http = http; _cfg = cfg; _resilience = resilience;
    }

    public async Task<SttResult> TranscribeAsync(Stream audio, string fileName, string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.SttApiKey))
            throw new InvalidOperationException(
                "Speech-to-text API key missing. Add it in ⚙ Settings → Voice.");

        // Buffer first: the resilience layer may retry, and each attempt needs a
        // fresh form + a fresh (rewound) file stream.
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var endpoint = _cfg.SttEndpoint.TrimEnd('/');

        using var httpResponse = await ProviderResilience.SendAsync(
            () =>
            {
                var form = new MultipartFormDataContent();
                var fileContent = new StreamContent(new MemoryStream(bytes));
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    GuessContentType(fileName));
                form.Add(fileContent, "file", fileName);
                form.Add(new StringContent(_cfg.SttModel), "model");
                form.Add(new StringContent("json"), "response_format");
                if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");

                var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/audio/transcriptions")
                {
                    Content = form
                };
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _cfg.SttApiKey);
                return req;
            },
            _http, _resilience, cancellationToken).ConfigureAwait(false);

        var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
            throw new LlmCallException((int)httpResponse.StatusCode,
                $"Speech-to-text backend returned {(int)httpResponse.StatusCode}. {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        return new SttResult(text, ProviderName);
    }

    internal static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".webm" => "audio/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".mp4" => "audio/mp4",
        ".m4a" => "audio/mp4",
        _ => "application/octet-stream"
    };

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed class OpenAiCompatibleTtsProvider : ITextToSpeechProvider
{
    public string ProviderName => VoiceSettings.OpenAiCompatible;

    private readonly HttpClient _http;
    private readonly VoiceSettings _cfg;
    private readonly ResilienceOptions _resilience;

    public OpenAiCompatibleTtsProvider(HttpClient http, VoiceSettings cfg, ResilienceOptions resilience)
    {
        _http = http; _cfg = cfg; _resilience = resilience;
    }

    public async Task<TtsResult> SynthesizeAsync(string text, string? voice = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.TtsApiKey))
            throw new InvalidOperationException(
                "Text-to-speech API key missing. Add it in ⚙ Settings → Voice.");

        var endpoint = _cfg.TtsEndpoint.TrimEnd('/');
        var payload = JsonSerializer.Serialize(new
        {
            model = _cfg.TtsModel,
            input = text,
            voice = string.IsNullOrWhiteSpace(voice) ? _cfg.TtsVoice : voice,
            response_format = "mp3"
        });

        using var httpResponse = await ProviderResilience.SendAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/audio/speech")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _cfg.TtsApiKey);
                return req;
            },
            _http, _resilience, cancellationToken).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new LlmCallException((int)httpResponse.StatusCode,
                $"Text-to-speech backend returned {(int)httpResponse.StatusCode}. {OpenAiCompatibleSttProvider.Truncate(body, 300)}");
        }

        var audio = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new TtsResult(audio, "audio/mpeg", ProviderName);
    }
}

// ── Voice service (picks the provider from Settings) ─────────────────────
public interface IVoiceService
{
    /// <summary>Server-side STT usable right now (enabled + supported provider).</summary>
    bool SttAvailable { get; }
    /// <summary>Server-side TTS usable right now.</summary>
    bool TtsAvailable { get; }
    Task<SttResult> SpeechToTextAsync(Stream audio, string fileName, string? language = null, CancellationToken ct = default);
    Task<TtsResult> TextToSpeechAsync(string text, string? voice = null, CancellationToken ct = default);
}

public sealed class VoiceService : IVoiceService
{
    public const string HttpClientName = "PixousAiVoice";

    private readonly SettingsStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ResilienceOptions _resilience;

    public VoiceService(SettingsStore store, IHttpClientFactory httpFactory, ResilienceOptions resilience)
    {
        _store = store; _httpFactory = httpFactory; _resilience = resilience;
    }

    private VoiceSettings Voice => _store.GetVoice();

    public bool SttAvailable
    {
        get
        {
            var v = Voice;
            return v.SttEnabled &&
                   (v.SttProvider == VoiceSettings.Echo || v.SttProvider == VoiceSettings.OpenAiCompatible);
        }
    }

    public bool TtsAvailable
    {
        get
        {
            var v = Voice;
            return v.TtsEnabled &&
                   (v.TtsProvider == VoiceSettings.Echo || v.TtsProvider == VoiceSettings.OpenAiCompatible);
        }
    }

    public Task<SttResult> SpeechToTextAsync(Stream audio, string fileName, string? language = null,
        CancellationToken ct = default)
    {
        var v = Voice;
        if (!v.SttEnabled)
            throw new InvalidOperationException(
                "Speech-to-text is disabled. Enable it in ⚙ Settings → Voice.");
        if (v.SttProvider == VoiceSettings.Browser)
            throw new InvalidOperationException(
                "Speech-to-text is set to Browser mode — the client uses the Web Speech API; no server call is made.");
        if (v.SttProvider == VoiceSettings.Echo)
            return new EchoSpeechToTextProvider().TranscribeAsync(audio, fileName, language, ct);
        if (v.SttProvider == VoiceSettings.OpenAiCompatible)
            return new OpenAiCompatibleSttProvider(
                _httpFactory.CreateClient(HttpClientName), v, _resilience)
                .TranscribeAsync(audio, fileName, language, ct);
        throw new InvalidOperationException($"Unknown speech-to-text provider '{v.SttProvider}'.");
    }

    public Task<TtsResult> TextToSpeechAsync(string text, string? voice = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must not be empty.", nameof(text));
        var v = Voice;
        if (!v.TtsEnabled)
            throw new InvalidOperationException(
                "Text-to-speech is disabled. Enable it in ⚙ Settings → Voice.");
        if (v.TtsProvider == VoiceSettings.Browser)
            throw new InvalidOperationException(
                "Text-to-speech is set to Browser mode — the client uses SpeechSynthesis; no server call is made.");
        if (v.TtsProvider == VoiceSettings.Echo)
            return new EchoTextToSpeechProvider().SynthesizeAsync(text, voice, ct);
        if (v.TtsProvider == VoiceSettings.OpenAiCompatible)
            return new OpenAiCompatibleTtsProvider(
                _httpFactory.CreateClient(HttpClientName), v, _resilience)
                .SynthesizeAsync(text, voice, ct);
        throw new InvalidOperationException($"Unknown text-to-speech provider '{v.TtsProvider}'.");
    }
}
