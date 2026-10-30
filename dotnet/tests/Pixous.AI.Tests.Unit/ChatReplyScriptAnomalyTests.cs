using Microsoft.Extensions.Logging.Abstractions;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>A flagged (wrong-script) translated chat reply is withheld; null/false are passed through unchanged.</summary>
public class ChatReplyScriptAnomalyTests
{
    private sealed class FixedChat : IChatProvider
    {
        public string ProviderName => "Fixed";
        public Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> history, ChatRequestOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse("ORIGINAL", ProviderName, "m"));
        public async IAsyncEnumerable<ChatChunk> StreamChatAsync(IReadOnlyList<ChatMessage> history, ChatRequestOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { yield return new ChatChunk("ORIGINAL"); await Task.CompletedTask; }
    }

    private sealed class FixedTranslator(bool? anomaly) : ITranslationProvider
    {
        public string ProviderName => "FixedT";
        public Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranslationResult("TRANSLATED", targetLanguage, sourceLanguage, ProviderName, anomaly));
        public Task<DetectedLanguage> DetectAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SupportedLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static ChatService Make(bool? anomaly) =>
        new(new StubChatFactory(new FixedChat()), new StubTranslationFactory(new FixedTranslator(anomaly)),
            TestOptions.Echo(), NullLogger<ChatService>.Instance);

    [Fact]
    public async Task Flagged_ReplyIsWithheld_OriginalReturned_FlagTrue()
    {
        var r = await Make(true).ChatAsync(new ChatRequestDto("hi", ResponseLanguage: "ta"));
        Assert.Equal("ORIGINAL", r.Reply);
        Assert.True(r.ReplyScriptAnomaly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task NotFlagged_TranslationReturned_FlagIsNotTrue(bool? anomaly)
    {
        var r = await Make(anomaly).ChatAsync(new ChatRequestDto("hi", ResponseLanguage: "ta"));
        Assert.Equal("TRANSLATED", r.Reply);
        Assert.Equal(anomaly, r.ReplyScriptAnomaly);
    }

    [Fact]
    public async Task NoResponseLanguage_NoTranslation_FlagNull()
    {
        var r = await Make(true).ChatAsync(new ChatRequestDto("hi"));
        Assert.Equal("ORIGINAL", r.Reply);
        Assert.Null(r.ReplyScriptAnomaly);
    }
}
