using Microsoft.Extensions.Logging.Abstractions;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Core.Stores;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

internal sealed class StubChatFactory : IChatProviderFactory
{
    private readonly IChatProvider _p;
    public StubChatFactory(IChatProvider p) => _p = p;
    public IChatProvider Get(string? providerName = null) => _p;
    public IEnumerable<string> RegisteredProviders => new[] { _p.ProviderName };
}

internal sealed class StubTranslationFactory : ITranslationProviderFactory
{
    private readonly ITranslationProvider _p;
    public StubTranslationFactory(ITranslationProvider p) => _p = p;
    public ITranslationProvider Get(string? providerName = null) => _p;
    public IEnumerable<string> RegisteredProviders => new[] { _p.ProviderName };
}

static class TestOptions
{
    public static AiSuiteOptions Echo() => new()
    {
        ChatProvider = "Echo",
        TranslationProvider = "Echo",
        Languages = new SupportedLanguagesOptions
        {
            Default = "en",
            Supported = new List<string> { "en", "ta", "es" }
        }
    };
}


internal sealed class CapturingChatProvider : IChatProvider
{
    public ChatRequestOptions? LastOptions;
    public string ProviderName => "Capturing";
    public Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> history, ChatRequestOptions? options = null, CancellationToken ct = default)
    { LastOptions = options; return Task.FromResult(new ChatResponse("ok", ProviderName, "m")); }
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(IReadOnlyList<ChatMessage> history, ChatRequestOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { LastOptions = options; yield return new ChatChunk("ok"); await Task.CompletedTask; }
}

public class ChatServiceTests
{
    private static ChatService Sut(IConversationStore? store = null) => new(
        new StubChatFactory(new EchoChatProvider()),
        new StubTranslationFactory(new EchoTranslationProvider()),
        TestOptions.Echo(),
        NullLogger<ChatService>.Instance,
        store);


    [Fact]
    public async Task ChatAsync_PassesTemperatureThroughToProvider()
    {
        var cap = new CapturingChatProvider();
        var sut = new ChatService(new StubChatFactory(cap), new StubTranslationFactory(new EchoTranslationProvider()),
            TestOptions.Echo(), NullLogger<ChatService>.Instance, null);
        await sut.ChatAsync(new ChatRequestDto("hello", Temperature: 0.0));
        Assert.Equal(0.0, cap.LastOptions?.Temperature);
        await sut.ChatAsync(new ChatRequestDto("hello"));
        Assert.Null(cap.LastOptions?.Temperature);
    }

    [Fact]
    public async Task ChatAsync_ReturnsEchoReply()
    {
        var res = await Sut().ChatAsync(new ChatRequestDto("hello"));
        Assert.Contains("hello", res.Reply);
        Assert.Equal("Echo", res.Provider);
    }

    [Fact]
    public async Task ChatAsync_EmptyMessage_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().ChatAsync(new ChatRequestDto("  ")));
    }

    [Fact]
    public async Task ChatAsync_UnsupportedLanguage_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().ChatAsync(new ChatRequestDto("hi", ResponseLanguage: "xx")));
    }

    [Fact]
    public async Task StreamChatAsync_AlwaysEndsWithFinalChunk()
    {
        var chunks = new List<ChatChunk>();
        await foreach (var c in Sut().StreamChatAsync(new ChatRequestDto("hi")))
            chunks.Add(c);
        Assert.NotEmpty(chunks);
        Assert.True(chunks[^1].IsFinal);
    }

    [Fact]
    public async Task ChatAsync_WithSessionId_PersistsConversation()
    {
        var store = new InMemoryConversationStore();
        var sut = Sut(store);
        await sut.ChatAsync(new ChatRequestDto("first", SessionId: "s1"));
        await sut.ChatAsync(new ChatRequestDto("second", SessionId: "s1"));
        var stored = await store.GetAsync("s1");
        // user, assistant, user, assistant
        Assert.Equal(4, stored.Count);
        Assert.Contains("second", stored[2].Content);
    }
}

public class TranslationServiceTests
{
    private static TranslationService Sut() => new(
        new StubTranslationFactory(new EchoTranslationProvider()),
        TestOptions.Echo(),
        NullLogger<TranslationService>.Instance);

    [Fact]
    public async Task TranslateAsync_ReturnsEchoTranslation()
    {
        var res = await Sut().TranslateAsync(new TranslateRequestDto("hello", "ta"));
        Assert.Equal("ta", res.TargetLanguage);
        Assert.Contains("hello", res.TranslatedText);
    }

    [Fact]
    public async Task TranslateAsync_UnsupportedLanguage_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().TranslateAsync(new TranslateRequestDto("hi", "xx")));
    }

    [Fact]
    public async Task TranslateBatchAsync_TranslatesEach()
    {
        var res = await Sut().TranslateBatchAsync(new TranslateBatchRequestDto(
            new List<string> { "hi", "bye" }, "es"));
        Assert.Equal(2, res.TranslatedTexts.Count);
        Assert.All(res.TranslatedTexts, t => Assert.Contains("[es]", t));
    }

    [Fact]
    public async Task TranslateBatchAsync_EmptyBatch_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().TranslateBatchAsync(new TranslateBatchRequestDto(new List<string>(), "es")));
    }

    [Fact]
    public async Task DetectAsync_ReturnsProviderResult()
    {
        var res = await Sut().DetectAsync(new DetectRequestDto("hello"));
        Assert.Equal("Echo", res.Provider);
    }
}

public class KnowledgeBaseProviderTests
{
    private static KnowledgeBaseChatProvider Sut() => new(new KnowledgeBaseOptions
    {
        FallbackAnswer = "fallback",
        Entries = new List<KnowledgeEntry>
        {
            new() { Match = new List<string> { "pricing", "cost" }, Answer = "pricing answer" },
            new() { Match = new List<string> { "contact", "email" }, Answer = "contact answer" },
        }
    });

    private static IReadOnlyList<ChatMessage> History(string text) =>
        new[] { new ChatMessage(ChatRole.User, text) };

    [Fact]
    public async Task MatchesBestEntry_CaseInsensitive()
    {
        var res = await Sut().ChatAsync(History("What is your PRICING?"));
        Assert.Equal("pricing answer", res.Content);
    }

    [Fact]
    public async Task ReturnsFallback_WhenNothingMatches()
    {
        var res = await Sut().ChatAsync(History("quantum bananas"));
        Assert.Equal("fallback", res.Content);
    }

    [Fact]
    public async Task Stream_EndsWithFinalChunk_AndReassemblesAnswer()
    {
        var sb = new System.Text.StringBuilder();
        var sawFinal = false;
        await foreach (var c in Sut().StreamChatAsync(History("contact please")))
        {
            sb.Append(c.Delta);
            sawFinal = c.IsFinal;
        }
        Assert.True(sawFinal);
        Assert.Contains("contact answer", sb.ToString());
    }
}

public class ConversationStoreTests
{
    [Fact]
    public async Task UnknownSession_ReturnsEmpty()
    {
        var s = new InMemoryConversationStore();
        Assert.Empty(await s.GetAsync("nope"));
    }

    [Fact]
    public async Task Append_ThenGet_PreservesOrder()
    {
        var s = new InMemoryConversationStore();
        await s.AppendAsync("a", new[] { new ChatMessage(ChatRole.User, "1") });
        await s.AppendAsync("a", new[] { new ChatMessage(ChatRole.Assistant, "2") });
        var got = await s.GetAsync("a");
        Assert.Equal(2, got.Count);
        Assert.Equal("2", got[1].Content);
    }

    [Fact]
    public async Task Clear_RemovesSession()
    {
        var s = new InMemoryConversationStore();
        await s.AppendAsync("a", new[] { new ChatMessage(ChatRole.User, "1") });
        await s.ClearAsync("a");
        Assert.Empty(await s.GetAsync("a"));
    }
}

public class RuntimeSelectionTests
{
    private static readonly string[] Chats = { "Echo", "KnowledgeBase" };
    private static readonly string[] Translators = { "Echo" };

    [Fact]
    public void Defaults_AreNull_MeaningUseConfiguration()
    {
        var s = new AiSuiteRuntimeSelection();
        Assert.Null(s.ChatProvider);
        Assert.Null(s.TranslationProvider);
    }

    [Fact]
    public void TrySet_ValidNames_Applies()
    {
        var s = new AiSuiteRuntimeSelection();
        Assert.Null(s.TrySet("KnowledgeBase", "Echo", Chats, Translators));
        Assert.Equal("KnowledgeBase", s.ChatProvider);
        Assert.Equal("Echo", s.TranslationProvider);
    }

    [Fact]
    public void TrySet_UnknownName_Rejects_AndKeepsOld()
    {
        var s = new AiSuiteRuntimeSelection();
        Assert.Null(s.TrySet("Echo", null, Chats, Translators));
        Assert.NotNull(s.TrySet("Nope", null, Chats, Translators));
        Assert.Equal("Echo", s.ChatProvider);
    }

    [Fact]
    public void Reset_ClearsOverrides()
    {
        var s = new AiSuiteRuntimeSelection();
        s.TrySet("KnowledgeBase", "Echo", Chats, Translators);
        s.Reset();
        Assert.Null(s.ChatProvider);
        Assert.Null(s.TranslationProvider);
    }
}

public class OptionsValidationTests
{
    [Fact]
    public void EchoDefaults_AreValid()
    {
        Assert.Empty(TestOptions.Echo().Validate());
    }

    [Fact]
    public void MissingChatProvider_FailsValidation()
    {
        var o = TestOptions.Echo();
        o.ChatProvider = "";
        Assert.NotEmpty(o.Validate());
    }

    [Fact]
    public void OpenAiSelected_WithoutEndpoint_FailsValidation()
    {
        var o = TestOptions.Echo();
        o.ChatProvider = "OpenAiCompatible";
        o.OpenAiCompatible.Endpoint = null;
        Assert.Contains(o.Validate(), e => e.Contains("Endpoint"));
    }

    [Fact]
    public void Languages_WithoutDefault_FailsValidation()
    {
        var o = TestOptions.Echo();
        o.Languages.Supported = new List<string> { "ta" }; // default "en" missing
        Assert.NotEmpty(o.Validate());
    }

    [Fact]
    public void BadResilience_FailsValidation()
    {
        var o = TestOptions.Echo();
        o.Resilience.MaxRetries = 99;
        Assert.NotEmpty(o.Validate());
    }

    [Fact]
    public void EnabledRateLimit_RequiresPositiveValues()
    {
        var o = TestOptions.Echo();
        o.RateLimit.Enabled = true;
        o.RateLimit.PermitLimit = 0;
        Assert.NotEmpty(o.Validate());
    }
}
