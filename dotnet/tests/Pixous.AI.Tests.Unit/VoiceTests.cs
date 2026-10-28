using System.Net;
using System.Text;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>Voice: Echo keyless providers, disabled/browser guards, OpenAI-compatible wire format.</summary>
public class VoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pixous-voice-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? LastUri;
        public string? LastBody;
        public bool LastWasMultipart;
        public string? LastMultipartBody;
        public readonly List<KeyValuePair<string, string?>> Headers = new();
        public string ResponseBody = """{"text":"hello from stt"}""";
        public byte[] ResponseBytes = { 1, 2, 3, 4 };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            foreach (var h in request.Headers) Headers.Add(new(h.Key, h.Value.FirstOrDefault()));
            if (request.Content is MultipartFormDataContent mp)
            {
                LastWasMultipart = true;
                LastMultipartBody = await mp.ReadAsStringAsync(ct);
                LastBody = LastMultipartBody;
            }
            else if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.Contains("speech")
                    ? new ByteArrayContent(ResponseBytes)
                    : new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FixedFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FixedFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private SettingsStore NewStore(VoiceSettings voice)
    {
        var store = new SettingsStore(_dir, new SettingsOptions(), new KnowledgeBaseOptions());
        store.SetVoice(voice);
        return store;
    }

    private static VoiceService Service(SettingsStore store, CaptureHandler handler) =>
        new(store, new FixedFactory(handler), new ResilienceOptions { MaxRetries = 0, BaseDelayMs = 0 });

    private static MemoryStream Audio() => new(new byte[] { 1, 2, 3, 4, 5 });

    // ── Echo (keyless demo) ──────────────────────────────────────────────
    [Fact]
    public async Task EchoStt_ReturnsDemoTranscript()
    {
        var provider = new EchoSpeechToTextProvider();
        var r = await provider.TranscribeAsync(Audio(), "a.webm");
        Assert.Contains("demo speech-to-text", r.Text);
        Assert.Equal("Echo", r.Provider);
    }

    [Fact]
    public async Task EchoTts_ReturnsPlayableWav()
    {
        var provider = new EchoTextToSpeechProvider();
        var r = await provider.SynthesizeAsync("hi there");
        Assert.Equal("audio/wav", r.ContentType);
        Assert.True(r.Audio.Length > 44, "has RIFF header + samples");
        Assert.Equal("RIFF", Encoding.ASCII.GetString(r.Audio, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(r.Audio, 8, 4));
    }

    // ── guards ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Stt_Disabled_Throws()
    {
        var svc = Service(NewStore(new VoiceSettings { SttEnabled = false }), new CaptureHandler());
        Assert.False(svc.SttAvailable);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SpeechToTextAsync(Audio(), "a.webm"));
        Assert.Contains("disabled", ex.Message);
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public async Task Stt_BrowserMode_ThrowsWithHint()
    {
        var svc = Service(NewStore(new VoiceSettings
        {
            SttEnabled = true, SttProvider = VoiceSettings.Browser
        }), new CaptureHandler());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SpeechToTextAsync(Audio(), "a.webm"));
        Assert.Contains("Browser", ex.Message);
    }

    [Fact]
    public async Task Tts_Disabled_Throws()
    {
        var svc = Service(NewStore(new VoiceSettings()), new CaptureHandler());
        Assert.False(svc.TtsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TextToSpeechAsync("hello"));
    }

    // ── OpenAI-compatible ────────────────────────────────────────────────
    [Fact]
    public async Task Stt_OpenAiCompatible_PostsMultipart_WithModelAndKey()
    {
        var handler = new CaptureHandler { ResponseBody = """{"text":"transcribed words"}""" };
        var svc = Service(NewStore(new VoiceSettings
        {
            SttEnabled = true,
            SttProvider = VoiceSettings.OpenAiCompatible,
            SttEndpoint = "http://api.example/v1",
            SttModel = "whisper-1",
            SttApiKey = "stt-secret"
        }), handler);

        Assert.True(svc.SttAvailable);
        var r = await svc.SpeechToTextAsync(Audio(), "note.mp3", "ta");

        Assert.Equal("transcribed words", r.Text);
        Assert.Equal("OpenAiCompatible", r.Provider);
        Assert.EndsWith("/audio/transcriptions", handler.LastUri!.AbsolutePath);
        Assert.NotNull(handler.LastMultipartBody);
        Assert.Contains("whisper-1", handler.LastMultipartBody);
        Assert.Contains("note.mp3", handler.LastMultipartBody);
        Assert.Contains("ta", handler.LastMultipartBody);
        Assert.Contains(handler.Headers, h => h.Key == "Authorization" && h.Value == "Bearer stt-secret");
    }

    [Fact]
    public async Task Stt_MissingKey_ThrowsBeforeNetwork()
    {
        var handler = new CaptureHandler();
        var svc = Service(NewStore(new VoiceSettings
        {
            SttEnabled = true,
            SttProvider = VoiceSettings.OpenAiCompatible,
            SttEndpoint = "http://api.example/v1"
        }), handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SpeechToTextAsync(Audio(), "a.webm"));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.LastUri); // never hit the network
    }

    [Fact]
    public async Task Tts_OpenAiCompatible_PostsJson_ReturnsAudio()
    {
        var handler = new CaptureHandler { ResponseBytes = new byte[] { 9, 9, 9 } };
        var svc = Service(NewStore(new VoiceSettings
        {
            TtsEnabled = true,
            TtsProvider = VoiceSettings.OpenAiCompatible,
            TtsEndpoint = "http://api.example/v1",
            TtsModel = "tts-1",
            TtsVoice = "nova",
            TtsApiKey = "tts-secret"
        }), handler);

        Assert.True(svc.TtsAvailable);
        var r = await svc.TextToSpeechAsync("hello world", voice: "echo");

        Assert.Equal("audio/mpeg", r.ContentType);
        Assert.Equal(new byte[] { 9, 9, 9 }, r.Audio);
        Assert.EndsWith("/audio/speech", handler.LastUri!.AbsolutePath);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("hello world", handler.LastBody);
        Assert.Contains("echo", handler.LastBody);      // per-call voice override
        Assert.Contains("tts-1", handler.LastBody);
        Assert.Contains(handler.Headers, h => h.Key == "Authorization" && h.Value == "Bearer tts-secret");
    }
}
