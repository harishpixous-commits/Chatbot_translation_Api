using System.Net;
using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>
/// Auto-fallback behaviour of the MultiLLM chain: an exhausted/invalid key
/// must transparently fall back to the next saved key (no vendor lock-in,
/// multiple keys at once).
/// </summary>
public class MultiLlmFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pixous-mlm-" + Guid.NewGuid().ToString("n"));
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Handler that answers per API key: key-one → status, key-two → 200.</summary>
    private sealed class KeyedHandler : HttpMessageHandler
    {
        public int FirstKeyStatus { get; set; } = 200;
        public int SecondKeyStatus { get; set; } = 200;
        public readonly List<string?> SeenKeys = new();
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.Authorization?.Parameter;
            SeenKeys.Add(key);
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var status = key == "key-one" ? FirstKeyStatus : SecondKeyStatus;
            if (status == 200)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"model":"test-model","choices":[{"message":{"content":"from-key-two"}}]}""",
                        Encoding.UTF8, "application/json")
                };

            var resp = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"message":"quota exceeded"}}""", Encoding.UTF8, "application/json")
            };
            if (status == 429)
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return resp;
        }
    }

    private sealed class FixedFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FixedFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private SettingsStore NewStore() => new(
        _dir,
        new SettingsOptions { ExhaustedCooldownSeconds = 60, InvalidKeyCooldownHours = 24 },
        new KnowledgeBaseOptions(),
        clock: () => _now);

    private static void AddTwoKeys(SettingsStore store)
    {
        store.AddKey(new LlmKeyEntry
        {
            Name = "primary", Vendor = "OpenAI",
            Endpoint = "https://api.one.example/v1", Model = "m-one", ApiKey = "key-one"
        });
        store.AddKey(new LlmKeyEntry
        {
            Name = "backup", Vendor = "OpenAI",
            Endpoint = "https://api.two.example/v1", Model = "m-two", ApiKey = "key-two"
        });
    }

    private static ResilienceOptions NewResilience(Action<ResilienceOptions>? configure = null)
    {
        var r = new ResilienceOptions { MaxRetries = 0, BaseDelayMs = 0, TimeoutSeconds = 5 };
        configure?.Invoke(r);
        return r;
    }

    private static MultiLlmChatProvider Chain(SettingsStore store, KeyedHandler handler,
        Action<ResilienceOptions>? configure = null) =>
        new(store, new FixedFactory(handler),
            NewResilience(configure));

    private static readonly IReadOnlyList<ChatMessage> Q =
        new[] { new ChatMessage(ChatRole.User, "hello") };

    [Fact]
    public async Task QuotaAnswer_429_IsHonored_NotEvaded_ByDefault()
    {
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 429 };
        var chain = Chain(store, handler);

        // Default: the vendor's quota answer is honored — the second key's
        // quota is NOT spent (no quota evasion).
        var ex = await Assert.ThrowsAsync<LlmCallException>(() => chain.ChatAsync(Q, new ChatRequestOptions()));
        Assert.Equal(429, ex.StatusNumber);
        Assert.Equal(new[] { "key-one" }, handler.SeenKeys);
        Assert.True(store.IsCooling(store.GetKeys()[0].Id), "429 key must be cooling");
    }

    [Fact]
    public async Task QuotaAnswer_429_FailsOverOnly_WhenOperatorOptsIn()
    {
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 429 };
        var chain = Chain(store, handler, r => r.AllowQuotaKeyFallback = true);

        var reply = await chain.ChatAsync(Q, new ChatRequestOptions(SystemPrompt: "be nice"));

        Assert.Equal("from-key-two", reply.Content);
        Assert.Equal(new[] { "key-one", "key-two" }, handler.SeenKeys);
        Assert.True(store.IsCooling(store.GetKeys()[0].Id), "429 key must be cooling");
        Assert.False(store.IsCooling(store.GetKeys()[1].Id));
    }

    [Fact]
    public async Task CoolingKey_IsSkippedOnNextCall()
    {
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 429 };
        var chain = Chain(store, handler, r => r.AllowQuotaKeyFallback = true);

        await chain.ChatAsync(Q, new ChatRequestOptions());
        handler.SeenKeys.Clear();

        var reply = await chain.ChatAsync(Q, new ChatRequestOptions());

        Assert.Equal("from-key-two", reply.Content);
        Assert.Single(handler.SeenKeys);           // exhausted key skipped entirely
        Assert.Equal("key-two", handler.SeenKeys[0]);

        // After the cool-down expires the key is retried automatically.
        _now = _now.AddSeconds(61);
        handler.SeenKeys.Clear();
        await chain.ChatAsync(Q, new ChatRequestOptions());
        Assert.Equal(2, handler.SeenKeys.Count);
    }

    [Fact]
    public async Task InvalidKey_401_MarksInvalid_AndFallsBack()
    {
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 401 };
        var chain = Chain(store, handler);

        var reply = await chain.ChatAsync(Q, new ChatRequestOptions());

        Assert.Equal("from-key-two", reply.Content);
        var (state, until, status, _) = store.GetKeyStatus(store.GetKeys()[0].Id);
        Assert.Equal("invalid", state);
        Assert.Equal(401, status);
        Assert.NotNull(until);
        Assert.True(until > _now.AddHours(20), "invalid key cooled for ~24h");
    }

    [Fact]
    public async Task Transient500_FallsBack_ButDoesNotCoolTheKey()
    {
        // Ambiguous fallback is an explicit opt-in (duplicate-charge risk).
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 500 };
        var chain = Chain(store, handler, r => { r.AllowAmbiguousKeyFallback = true; r.AllowQuotaKeyFallback = true; });

        var reply = await chain.ChatAsync(Q, new ChatRequestOptions());

        Assert.Equal("from-key-two", reply.Content);
        Assert.False(store.IsCooling(store.GetKeys()[0].Id), "5xx is transient — no cool-down");
        var (state, _, _, _) = store.GetKeyStatus(store.GetKeys()[0].Id);
        // No cooling, but the vendor never accepted the key: "untested", not a green "ok".
        Assert.Equal("untested", state);
    }

    [Fact]
    public async Task AllKeysFail_ThrowsSecretFreeSummary()
    {
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 429, SecondKeyStatus = 500 };
        var chain = Chain(store, handler, r => { r.AllowAmbiguousKeyFallback = true; r.AllowQuotaKeyFallback = true; });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => chain.ChatAsync(Q, new ChatRequestOptions()));
        Assert.Contains("All configured LLM keys failed", ex.Message);
        Assert.Contains("primary", ex.Message);
        Assert.Contains("backup", ex.Message);
        Assert.DoesNotContain("key-one", ex.Message); // never leak raw keys
        Assert.DoesNotContain("key-two", ex.Message);
    }

    [Fact]
    public async Task NoKeys_Configured_GivesActionableMessage()
    {
        var store = NewStore();
        var chain = Chain(store, new KeyedHandler());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => chain.ChatAsync(Q, new ChatRequestOptions()));
        Assert.Contains("Settings", ex.Message);
        Assert.Contains("LLM APIs", ex.Message);
    }

    [Fact]
    public async Task DisabledKeys_AreNotUsed()
    {
        var store = NewStore();
        AddTwoKeys(store);
        store.UpdateKey(store.GetKeys()[0].Id, k => k.Enabled = false);
        var handler = new KeyedHandler { FirstKeyStatus = 500 };
        var chain = Chain(store, handler);

        var reply = await chain.ChatAsync(Q, new ChatRequestOptions());

        Assert.Equal("from-key-two", reply.Content);
        Assert.Single(handler.SeenKeys);
        Assert.Equal("key-two", handler.SeenKeys[0]);
    }

    [Fact]
    public async Task UploadedKnowledge_IsInjectedAsGroundedContext()
    {
        var store = NewStore();
        AddTwoKeys(store);
        store.AddDocument("physics.md", 120, new List<KnowledgeChunk>
        {
            new()
            {
                Text = "Quantum entanglement links particles so measuring one instantly affects the other.",
                Keywords = new List<string> { "quantum", "entanglement", "particles" }
            }
        });
        var handler = new KeyedHandler();
        var chain = Chain(store, handler);

        await chain.ChatAsync(
            new[] { new ChatMessage(ChatRole.User, "what is quantum entanglement?") },
            new ChatRequestOptions(SystemPrompt: "You are an agent."));

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var sys = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("You are an agent.", sys);
        Assert.Contains("Grounded knowledge", sys);
        Assert.Contains("Quantum entanglement links particles", sys);
    }

    [Fact]
    public async Task Transient500_Default_StopsWithoutFallbackOrCooldown()
    {
        // Default-deny: a 5xx may mean the provider processed (and billed) the
        // prompt — the chain must NOT re-send it to the next key.
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new KeyedHandler { FirstKeyStatus = 500 };
        var chain = Chain(store, handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => chain.ChatAsync(Q, new ChatRequestOptions()));
        Assert.Contains("may still", ex.Message);
        Assert.Contains("AllowAmbiguousKeyFallback", ex.Message);
        Assert.Single(handler.SeenKeys);            // second key never called
        Assert.Equal("key-one", handler.SeenKeys[0]);
        Assert.False(store.IsCooling(store.GetKeys()[0].Id), "5xx is transient — no cool-down");
    }

    [Fact]
    public async Task NetworkError_Default_StopsWithoutFallback()
    {
        var store = NewStore();
        AddTwoKeys(store);
        var handler = new ThrowingHandler();
        var chain = new MultiLlmChatProvider(store, new FixedFactory(handler), NewResilience());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => chain.ChatAsync(Q, new ChatRequestOptions()));
        Assert.Contains("may still", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("simulated network failure");
        }
    }

    [Fact]
    public void VendorFactory_PicksNativeAdapters()
    {
        var http = new HttpClient(new KeyedHandler());
        var res = new ResilienceOptions();

        Assert.IsType<AnthropicChatProvider>(LlmVendor.Create(http,
            new LlmKeyEntry { Vendor = "Anthropic", Endpoint = "https://api.anthropic.com", Model = "m", ApiKey = "k" }, res));
        Assert.IsType<GeminiChatProvider>(LlmVendor.Create(http,
            new LlmKeyEntry { Vendor = "Gemini", Endpoint = "https://x", Model = "m", ApiKey = "k" }, res));
        Assert.IsType<OpenAiCompatibleChatProvider>(LlmVendor.Create(http,
            new LlmKeyEntry { Vendor = "Groq", Endpoint = "https://x", Model = "m", ApiKey = "k" }, res));
        Assert.IsType<OpenAiCompatibleChatProvider>(LlmVendor.Create(http,
            new LlmKeyEntry { Vendor = "Ollama", Endpoint = "http://localhost:11434/v1", Model = "m" }, res));
    }
}
