using System.Net;
using System.Text;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers;
using Pixous.AI.Providers.Resilience;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>Real incremental SSE streaming across adapters + MultiLLM failover rules.</summary>
public class StreamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pixous-sse-" + Guid.NewGuid().ToString("n"));
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _fn;
        public string? LastBody;
        public string? LastUri;
        public StaticHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> fn) => _fn = fn;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri?.ToString();
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return _fn(request, LastBody);
        }
    }

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

    private static ResilienceOptions Resilience(Action<ResilienceOptions>? cfg = null)
    {
        var r = new ResilienceOptions { MaxRetries = 0, BaseDelayMs = 0, TimeoutSeconds = 5 };
        cfg?.Invoke(r);
        return r;
    }

    private static async Task<List<ChatChunk>> Collect(IAsyncEnumerable<ChatChunk> s)
    {
        var list = new List<ChatChunk>();
        await foreach (var c in s) list.Add(c);
        return list;
    }

    [Fact]
    public async Task SseReader_Parses_DataLines_And_Terminator()
    {
        var raw = ": comment\n\ndata: one\n\ndata: two\ndata: continued\n\ndata: [DONE]\n\ndata: after-done\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var payloads = new List<string>();
        await foreach (var p in SseReader.ReadDataAsync(ms)) payloads.Add(p);
        Assert.Equal(new[] { "one", "two\ncontinued" }, payloads);
    }

    [Fact]
    public async Task OpenAiCompatible_Streams_Deltas_Incrementally()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new OpenAiCompatibleChatProvider(new HttpClient(handler),
            new OpenAiCompatibleOptions { Endpoint = "https://x/v1", Model = "m", RequireApiKey = false },
            Resilience(), null);

        var chunks = await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Equal(new[] { "Hel", "lo" }, chunks.Where(c => !c.IsFinal).Select(c => c.Delta));
        Assert.True(chunks[^1].IsFinal);
        Assert.Contains("\"stream\":true", handler.LastBody);
    }

    [Fact]
    public async Task OpenAiCompatible_Stream_NonSuccess_Throws_LlmCallException()
    {
        var handler = new StaticHandler((_, _) => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{\"error\":\"quota\"}"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9)) }
        });
        var provider = new OpenAiCompatibleChatProvider(new HttpClient(handler),
            new OpenAiCompatibleOptions { Endpoint = "https://x/v1", Model = "m", RequireApiKey = false },
            Resilience(), null);

        var ex = await Assert.ThrowsAsync<LlmCallException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
        Assert.Equal(429, ex.StatusNumber!.Value);
        Assert.Equal(9, ex.RetryAfterSeconds!.Value);
    }

    [Fact]
    public async Task Anthropic_Streams_ContentBlockDeltas()
    {
        var sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Van\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"akkam\"}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new AnthropicChatProvider(new HttpClient(handler),
            new LlmKeyEntry { Name = "a", Vendor = "Anthropic", Endpoint = "https://api.anthropic.com", Model = "claude-x", ApiKey = "k" },
            Resilience());

        var chunks = await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Equal(new[] { "Van", "akkam" }, chunks.Where(c => !c.IsFinal).Select(c => c.Delta));
        Assert.Contains("\"stream\":true", handler.LastBody);
    }

    [Fact]
    public async Task Gemini_Streams_CandidateParts()
    {
        var sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"An\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"na\"}]}}]}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new GeminiChatProvider(new HttpClient(handler),
            new LlmKeyEntry { Name = "g", Vendor = "Gemini", Endpoint = "https://generativelanguage.googleapis.com/v1beta", Model = "gemini-x", ApiKey = "k" },
            Resilience());

        var chunks = await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Equal(new[] { "An", "na" }, chunks.Where(c => !c.IsFinal).Select(c => c.Delta));
        Assert.Contains("streamGenerateContent?alt=sse", handler.LastUri ?? string.Empty);
    }

    [Fact]
    public async Task Anthropic_MalformedJsonFrame_Throws_NoSilentHole()
    {
        var sse =
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Van\"}}\n\n" +
            "data: {broken frame\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"akkam\"}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new AnthropicChatProvider(new HttpClient(handler),
            new LlmKeyEntry { Name = "a", Vendor = "Anthropic", Endpoint = "https://api.anthropic.com", Model = "claude-x", ApiKey = "k" },
            Resilience());

        await Assert.ThrowsAsync<System.IO.InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
    }

    [Fact]
    public async Task Anthropic_MidStreamErrorEvent_Throws_AsStreamAbort()
    {
        var sse =
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Van\"}}\n\n" +
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new AnthropicChatProvider(new HttpClient(handler),
            new LlmKeyEntry { Name = "a", Vendor = "Anthropic", Endpoint = "https://api.anthropic.com", Model = "claude-x", ApiKey = "k" },
            Resilience());

        var ex = await Assert.ThrowsAsync<System.IO.InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
        Assert.DoesNotContain("Overloaded", ex.Message);
        Assert.Contains("stream error event", ex.Message);
    }

    [Fact]
    public async Task Gemini_MalformedJsonFrame_Throws_NoSilentHole()
    {
        var sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"An\"}]}}]}\n\n" +
            "data: {broken frame\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"na\"}]}}]}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new GeminiChatProvider(new HttpClient(handler),
            new LlmKeyEntry { Name = "g", Vendor = "Gemini", Endpoint = "https://generativelanguage.googleapis.com/v1beta", Model = "gemini-x", ApiKey = "k" },
            Resilience());

        await Assert.ThrowsAsync<System.IO.InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
    }

    [Fact]
    public async Task Gemini_MidStreamErrorJson_Throws_WithProviderMessage()
    {
        // Gemini can send {"error": {...}} as a data frame. Candidates are
        // absent, so the old code skipped it SILENTLY and ended the stream as
        // if complete - the worst hole shape (no text missing, just no answer).
        var sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"An\"}]}}]}\n\n" +
            "data: {\"error\":{\"code\":500,\"message\":\"backend exploded\"}}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new GeminiChatProvider(new HttpClient(handler),
            new LlmKeyEntry { Name = "g", Vendor = "Gemini", Endpoint = "https://generativelanguage.googleapis.com/v1beta", Model = "gemini-x", ApiKey = "k" },
            Resilience());

        var ex = await Assert.ThrowsAsync<System.IO.InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
        Assert.DoesNotContain("backend exploded", ex.Message);
        Assert.Contains("error frame mid-stream", ex.Message);
    }

    [Fact]
    public async Task SseReader_Eof_Without_Terminator_Throws()
    {
        // Upstream died mid-SSE (observed: llama-cpp-python aborts the body when
        // the prompt exceeds the context window). EOF without [DONE] must fail
        // closed, never surface as a completed empty reply.
        var raw = "data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in SseReader.ReadDataAsync(ms)) { }
        });
    }

    [Fact]
    public async Task SseReader_Trailing_Terminator_Without_BlankLine_DoesNotThrow()
    {
        var raw = "data: one\n\ndata: [DONE]"; // terminator in the final flush, no trailing blank line
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var payloads = new List<string>();
        await foreach (var p in SseReader.ReadDataAsync(ms)) payloads.Add(p);
        Assert.Equal(new[] { "one" }, payloads);
    }

    [Fact]
    public async Task OpenAiCompatible_Stream_AbortedWithoutDone_Throws()
    {
        var handler = new StaticHandler((_, _) => Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n\n")); // no [DONE]: upstream crashed
        var provider = new OpenAiCompatibleChatProvider(
            new HttpClient(handler),
            new OpenAiCompatibleOptions
            {
                Endpoint = "http://127.0.0.1:8085/v1",
                Model = "m",
                RequireApiKey = false,
            },
            Resilience(),
            apiKey: null);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
    }

    [Fact]
    public async Task OpenAiCompatible_MalformedJsonFrame_Throws_NoSilentHole()
    {
        // A corrupt `data:` frame mid-stream must abort fail-closed. Silently
        // skipping it would complete the answer with a HOLE: the user sees a
        // "successful" reply missing text, with no error signal.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {this is not json\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new OpenAiCompatibleChatProvider(new HttpClient(handler),
            new OpenAiCompatibleOptions { Endpoint = "https://x/v1", Model = "m", RequireApiKey = false },
            Resilience(), null);

        await Assert.ThrowsAsync<System.IO.InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
    }

    [Fact]
    public async Task OpenAiCompatible_MidStreamErrorJson_Throws_WithProviderMessage()
    {
        // OpenAI-compatible servers can emit {"error": ...} as a data frame
        // mid-stream. Surface the provider's message; never die on a cryptic
        // KeyNotFoundException from GetProperty("choices").
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"error\":{\"message\":\"model crashed\"}}\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new OpenAiCompatibleChatProvider(new HttpClient(handler),
            new OpenAiCompatibleOptions { Endpoint = "https://x/v1", Model = "m", RequireApiKey = false },
            Resilience(), null);

        var ex = await Assert.ThrowsAsync<System.IO.InvalidDataException>(async () =>
            await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
        // FIXED message: vendor error text (can echo private request content)
        // is never persisted in exceptions/logs.
        Assert.DoesNotContain("model crashed", ex.Message);
        Assert.Contains("error frame mid-stream", ex.Message);
    }

    [Fact]
    public async Task OpenAiCompatible_MetadataOnlyFrames_Skipped_NoThrow()
    {
        // Legit non-content frames: usage-only final frame (choices: []) and
        // any valid JSON object without choices. Neither can carry content, so
        // skipping them cannot create a hole. Previously these threw
        // KeyNotFoundException / IndexOutOfRangeException.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"total_tokens\":9}}\n\n" +
            "data: {\"usage\":{\"total_tokens\":9}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StaticHandler((_, _) => Sse(sse));
        var provider = new OpenAiCompatibleChatProvider(new HttpClient(handler),
            new OpenAiCompatibleOptions { Endpoint = "https://x/v1", Model = "m", RequireApiKey = false },
            Resilience(), null);

        var chunks = await Collect(provider.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Equal(new[] { "Hi" }, chunks.Where(c => !c.IsFinal).Select(c => c.Delta));
        Assert.True(chunks[^1].IsFinal);
    }

    [Fact]
    public async Task SseReader_Oversized_Frame_Throws()
    {
        var big = new string('x', 1_100_000);
        var raw = $"data: {big}\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in SseReader.ReadDataAsync(ms)) { }
        });
    }

    [Fact]
    public async Task MultiLlm_Stream_429_IsHonored_NoFailover_ByDefault()
    {
        var store = NewStore(); AddTwo(store);
        var calls = 0;
        var handler = new StaticHandler((_, _) =>
        {
            calls++;
            return new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":\"quota exhausted\"}"),
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) }
            };
        });
        var chain = new MultiLlmChatProvider(store, new FixedFactory(handler), Resilience());

        var ex = await Assert.ThrowsAsync<LlmCallException>(async () =>
            await Collect(chain.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
        Assert.Equal(429, ex.StatusNumber);
        Assert.Equal(1, calls); // second key's quota NOT spent
        var first = store.GetKeys()[0];
        Assert.Equal("cooling", store.GetKeyStatus(first.Id).State);
    }

    [Fact]
    public async Task MultiLlm_Stream_CallerCancellation_Surfaces_NoFailover()
    {
        var store = NewStore(); AddTwo(store);
        var calls = 0;
        var handler = new StaticHandler((_, _) =>
        {
            calls++;
            return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"one\"}}]}\n\n" +
                       "data: {\"choices\":[{\"delta\":{\"content\":\"two\"}}]}\n\ndata: [DONE]\n\n");
        });
        var chain = new MultiLlmChatProvider(store, new FixedFactory(handler), Resilience());

        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in chain.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, null, cts.Token))
            {
                if (c.IsFinal) continue;
                seen.Add(c.Delta);
                cts.Cancel(); // caller cancels after the first token
            }
        });
        Assert.Equal(new[] { "one" }, seen);
        Assert.Equal(1, calls); // cancellation is NOT a failover trigger
    }

    // ── MultiLLM streaming failover ─────────────────────────────────────

    private SettingsStore NewStore() => new(_dir,
        new SettingsOptions { ExhaustedCooldownSeconds = 60, InvalidKeyCooldownHours = 24 },
        new KnowledgeBaseOptions(), clock: () => DateTime.UtcNow);

    private void AddTwo(SettingsStore store)
    {
        store.AddKey(new LlmKeyEntry { Name = "primary", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m1", ApiKey = "key-one" });
        store.AddKey(new LlmKeyEntry { Name = "backup", Vendor = "OpenAI", Endpoint = "https://b/v1", Model = "m2", ApiKey = "key-two" });
    }

    private sealed class FixedFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public FixedFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    [Fact]
    public async Task MultiLlm_Stream_DefinitiveRejection_BeforeOutput_FallsToNextKey()
    {
        var store = NewStore(); AddTwo(store);
        var handler = new StaticHandler((req, _) =>
        {
            var key = req.Headers.Authorization?.Parameter;
            if (key == "key-one")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("{\"error\":\"bad key\"}") };
            return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"streamed\"}}]}\n\ndata: [DONE]\n\n");
        });
        var chain = new MultiLlmChatProvider(store, new FixedFactory(handler), Resilience());

        var chunks = await Collect(chain.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Equal("streamed", string.Concat(chunks.Where(c => !c.IsFinal).Select(c => c.Delta)));
        var first = store.GetKeys()[0];
        Assert.Equal("invalid", store.GetKeyStatus(first.Id).State);
    }

    [Fact]
    public async Task MultiLlm_Stream_Ambiguous_BeforeOutput_DoesNotFailOver_ByDefault()
    {
        var store = NewStore(); AddTwo(store);
        var calls = 0;
        var handler = new StaticHandler((req, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("{\"error\":\"boom\"}") };
        });
        var chain = new MultiLlmChatProvider(store, new FixedFactory(handler), Resilience());

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await Collect(chain.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") })));
        Assert.Equal(1, calls); // second key never contacted
    }

    [Fact]
    public async Task MultiLlm_Stream_MidStreamFailure_Surfaces_NoFailover()
    {
        var store = NewStore(); AddTwo(store);
        var calls = 0;
        var handler = new StaticHandler((req, _) =>
        {
            calls++;
            // One good chunk, then the body read throws (connection drop).
            var half = new MemoryStream(Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"));
            var failing = new FailingStream(half);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(failing) };
        });
        var chain = new MultiLlmChatProvider(store, new FixedFactory(handler), Resilience());

        var seen = new List<string>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var c in chain.StreamChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }))
                if (!c.IsFinal) seen.Add(c.Delta);
        });
        Assert.Equal(new[] { "partial" }, seen); // partial output WAS shown...
        Assert.Equal(1, calls);                  // ...so no failover was attempted
    }

    private sealed class FailingStream : Stream
    {
        private readonly Stream _inner; private bool _failed;
        public FailingStream(Stream inner) => _inner = inner;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_failed) throw new HttpRequestException("connection dropped mid-stream");
            var n = await _inner.ReadAsync(buffer, ct);
            if (n == 0) { _failed = true; throw new HttpRequestException("connection dropped mid-stream"); }
            return n;
        }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
