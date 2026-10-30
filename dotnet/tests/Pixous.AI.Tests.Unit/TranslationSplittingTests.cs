using System.Net;
using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

public sealed class TranslationSplittingTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(37)]
    [InlineData(200)]
    public void Split_ConcatenatesToInput_AndRespectsMax(int max)
    {
        var text = string.Concat(Enumerable.Range(1, 40).Select(i => $"Sentence number {i} is here. ")) + "Last one without end";
        var pieces = TranslationTextSplitter.Split(text, max);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => Assert.True(p.Length <= max && p.Length > 0));
    }

    [Fact]
    public void Split_PrefersSentenceBoundaries()
    {
        var pieces = TranslationTextSplitter.Split("One two three. Four five six. Seven eight nine.", 30);
        Assert.Equal(new[] { "One two three. Four five six. ", "Seven eight nine." }, pieces);
    }

    [Fact]
    public void Split_DevanagariDanda_AndNewline()
    {
        var pieces = TranslationTextSplitter.Split("नमस्ते दुनिया। यह परीक्षा है।\nतीसरी पंक्ति यहाँ", 22);
        Assert.Equal("नमस्ते दुनिया। यह परीक्षा है।\nतीसरी पंक्ति यहाँ", string.Concat(pieces));
        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.True(p.Length <= 22));
    }

    [Fact]
    public void Split_NoWhitespace_HardCut_NeverSplitsSurrogatePair()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F331", 50)); // 100 UTF-16 units
        var pieces = TranslationTextSplitter.Split(text, 7);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => { Assert.True(p.Length <= 7); Assert.False(char.IsHighSurrogate(p[^1])); Assert.False(char.IsLowSurrogate(p[0])); });
    }

    [Fact]
    public void Split_ShortText_SinglePiece() =>
        Assert.Equal(new[] { "short" }, TranslationTextSplitter.Split("short", 2000));

    private sealed class Echo : HttpMessageHandler
    {
        public List<string[]> Calls = new();
        public bool WrongCount;
        public int HealthCalls;
        public string Health = "{\"status\":\"ok\"}";
        public string Quality = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                HealthCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Health, Encoding.UTF8, "application/json") };
            }
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Calls.Add(items);
            var outs = items.Select(i => "T(" + i + ")").ToList();
            if (WrongCount) outs.RemoveAt(0);
            var body = JsonSerializer.Serialize(new { translations = outs }).TrimEnd('}') + Quality + "}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static PixousSelfHostedTranslationProvider P(Echo h, int max = 50) =>
        new(new HttpClient(h), new PixousEngineOptions { Endpoint = "http://127.0.0.1:8765", MaxItemChars = max },
            new ResilienceOptions { TimeoutSeconds = 10, MaxRetries = 0 }, null);

    [Fact]
    public async Task LongText_SentAsOrderedItemsInOneCall_RejoinedWithOriginalWhitespace()
    {
        var h = new Echo();
        var text = "Alpha one. Beta two.  Gamma three.\nDelta four. Epsilon five.";
        var r = await P(h, 30).TranslateAsync(text, "ta", "en");
        Assert.Single(h.Calls);
        var pieces = TranslationTextSplitter.Split(text, 30);
        Assert.True(pieces.Count >= 2);
        Assert.Equal(pieces.Select(p => p.TrimEnd()).ToArray(), h.Calls[0]);
        var expected = string.Concat(pieces.Select(p => "T(" + p.TrimEnd() + ")" + p[p.TrimEnd().Length..]));
        Assert.Equal(expected, r.TranslatedText);
    }

    [Fact]
    public async Task ShortText_SentUnchangedAsOneItem()
    {
        var h = new Echo();
        var r = await P(h, 2000).TranslateAsync("Hello there.  ", "ta", "en");
        Assert.Equal(new[] { "Hello there.  " }, h.Calls[0]);
        Assert.Equal("T(Hello there.  )", r.TranslatedText);
    }

    [Fact]
    public async Task ItemCountMismatch_FailsClosed_NoPartialText()
    {
        var h = new Echo { WrongCount = true };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => P(h, 30).TranslateAsync("Alpha one. Beta two. Gamma three. Delta four.", "ta", "en"));
        Assert.DoesNotContain("Alpha", ex.Message);
    }

    [Fact]
    public async Task AnomalyInAnyPiece_FlagsWholeResult()
    {
        var h = new Echo { Quality = ",\"quality\":{\"checked\":true,\"script_anomaly_items\":[1]}" };
        var r = await P(h, 30).TranslateAsync("Alpha one. Beta two. Gamma three. Delta four.", "ta", "en");
        Assert.True(r.ScriptAnomaly);
    }

    [Fact]
    public async Task CancelledBeforeSend_NoCall()
    {
        var h = new Echo();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => P(h, 30).TranslateAsync("Alpha one. Beta two. Gamma three.", "ta", "en", cts.Token));
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData("Price is 3.14159 dollars and 2.71828 euros today, ok", "3.14159")]
    [InlineData("See https://example.com/a.b/c.html?x=1.2 for more details here", "https://example.com/a.b/c.html?x=1.2")]
    [InlineData("Call obj.method(arg.value) then run file.name.txt now please", "obj.method(arg.value)")]
    public void Split_NoWhitespaceAfterDot_DecimalsUrlsCode_NotCutWhenWhitespaceCutAvailable(string text, string token)
    {
        var pieces = TranslationTextSplitter.Split(text, 40);
        Assert.Equal(text, string.Concat(pieces));
        Assert.Contains(pieces, p => p.Contains(token));   // the token stays whole in one piece
        Assert.All(pieces, p => Assert.True(p.Length <= 40));
    }

    [Fact]
    public void Split_Abbreviation_KNOWN_LIMITATION_CanCutAfterDotSpace()
    {
        // "Dr. " looks like a sentence end: documented false boundary, pieces are still lossless.
        var text = "Please ask Dr. Rao about the report. He knows the details well.";
        var pieces = TranslationTextSplitter.Split(text, 16);
        Assert.Equal(text, string.Concat(pieces));
        Assert.Contains(pieces, p => p.EndsWith("Dr. "));
    }

    [Fact]
    public void Split_Crlf_LosslessAndWithinMax()
    {
        var text = string.Concat(Enumerable.Range(1, 30).Select(i => $"Line {i} here\r\n"));
        var pieces = TranslationTextSplitter.Split(text, 25);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => Assert.True(p.Length <= 25));
        Assert.All(pieces.Take(pieces.Count - 1), p => Assert.EndsWith("\n", p));
    }

    [Fact]
    public void Split_MixedSurrogatesAndNonNativePunctuation_LosslessNoSplitPair()
    {
        var text = string.Concat(Enumerable.Range(1, 20).Select(i => $"உரை {i}\U0001F331\u0BE6। வணக்கம்! "));
        var pieces = TranslationTextSplitter.Split(text, 19);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => { Assert.True(p.Length <= 19); Assert.False(char.IsHighSurrogate(p[^1])); Assert.False(char.IsLowSurrogate(p[0])); });
    }

    private sealed class Status413 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("{\"detail\":\"SECRET\"}") });
    }

    [Fact]
    public async Task SplitPiecesRejected413_IsPreciseConfigMismatch_NotCallerValidation_NoBodyForwarded()
    {
        var p = new PixousSelfHostedTranslationProvider(new HttpClient(new Status413()),
            new PixousEngineOptions { Endpoint = "http://127.0.0.1:8765", MaxItemChars = 30 },
            new ResilienceOptions { TimeoutSeconds = 10, MaxRetries = 0 }, null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.TranslateAsync("Alpha one. Beta two. Gamma three. Delta four.", "ta", "en"));
        Assert.Contains("MaxItemChars", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.DoesNotContain("Alpha", ex.Message);
    }

    private sealed class Fac : ITranslationProviderFactory
    {
        private readonly ITranslationProvider _p;
        public Fac(ITranslationProvider p) => _p = p;
        public ITranslationProvider Get(string? providerName = null) => _p;
        public IEnumerable<string> RegisteredProviders => new[] { _p.ProviderName };
    }

    [Fact]
    public async Task BatchRoute_RealProviderPath_OneSidecarRequestPerText_LongTextSplit_EmptySkipped_AnomalyIndexPropagates()
    {
        // Actual call path: TranslationService.TranslateBatchAsync -> (sequential per text) provider.TranslateAsync -> split -> one /translate request per non-empty text.
        var h = new Echo { Quality = ",\"quality\":{\"checked\":true,\"script_anomaly_items\":[1]}" };
        var svc = new TranslationService(new Fac(P(h, 30)), TestOptions.Echo(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TranslationService>.Instance);
        var longText = "Alpha one. Beta two. Gamma three. Delta four. Epsilon five.";
        var res = await svc.TranslateBatchAsync(new TranslateBatchRequestDto(new List<string> { "Short.", "  ", longText }, "ta", "en"));
        Assert.Equal(2, h.Calls.Count);                       // whitespace-only text is not sent
        Assert.Equal(new[] { "Short." }, h.Calls[0]);
        Assert.True(h.Calls[1].Length >= 2);                  // long text became several ordered items of ONE request
        Assert.Equal(3, res.TranslatedTexts.Count);
        Assert.Equal(string.Empty, res.TranslatedTexts[1]);
        Assert.Equal(new[] { 0, 2 }, res.ScriptAnomalyIndexes!.ToArray());
    }

    [Fact]
    public async Task SidecarHealthLimitsBelowMaxItemChars_PreciseConfigError_BeforeAnyTranslateRequest_AndLimitsCached()
    {
        var h = new Echo { Health = "{\"status\":\"ok\",\"limits\":{\"max_items\":256,\"max_item_chars\":20,\"max_total_chars\":200000}}" };
        var p = P(h, 30);
        var t = "Alpha one. Beta two. Gamma three. Delta four.";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.TranslateAsync(t, "ta", "en"));
        Assert.Contains("PIXOUS_MAX_ITEM_CHARS (20)", ex.Message);
        Assert.DoesNotContain("Alpha", ex.Message);
        Assert.Empty(h.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.TranslateAsync(t, "ta", "en"));
        Assert.Equal(1, h.HealthCalls);   // success cached, not re-read per request
    }

    [Fact]
    public async Task SidecarHealthLimitsCompatible_Proceeds_PieceCountAboveMaxItems_Rejected()
    {
        var ok = new Echo { Health = "{\"limits\":{\"max_items\":256,\"max_item_chars\":2000,\"max_total_chars\":200000}}" };
        await P(ok, 30).TranslateAsync("Alpha one. Beta two. Gamma three. Delta four.", "ta", "en");
        Assert.Single(ok.Calls);
        var few = new Echo { Health = "{\"limits\":{\"max_items\":1,\"max_item_chars\":2000,\"max_total_chars\":200000}}" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => P(few, 30).TranslateAsync("Alpha one. Beta two. Gamma three. Delta four.", "ta", "en"));
        Assert.Contains("PIXOUS_MAX_ITEMS (1)", ex.Message);
        Assert.Empty(few.Calls);
    }
}
