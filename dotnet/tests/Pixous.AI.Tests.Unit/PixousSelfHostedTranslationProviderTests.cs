using Pixous.AI.Abstractions;
using System.Net;
using System.Text;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

public sealed class PixousSelfHostedTranslationProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = """{"translations":["எங்கள் வலைத்தளத்திற்கு வரவேற்கிறோம்."],"pivot":false,"provider":"PixousSelfHosted"}""";
        public string? LastBody;
        public string? SeenToken;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenToken = request.Headers.Authorization?.Parameter;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static PixousSelfHostedTranslationProvider NewProvider(StubHandler handler, string? token = null) =>
        new(new HttpClient(handler),
            new PixousEngineOptions { Endpoint = "http://127.0.0.1:8765" },
            new ResilienceOptions { TimeoutSeconds = 10, MaxRetries = 0 },
            token);

    [Fact]
    public async Task Translate_PostsItemsShape_AndReturnsFirstTranslation()
    {
        var handler = new StubHandler();
        var provider = NewProvider(handler);
        var result = await provider.TranslateAsync("Welcome to our website.", "ta", "en");
        Assert.Equal("எங்கள் வலைத்தளத்திற்கு வரவேற்கிறோம்.", result.TranslatedText);
        Assert.Equal("PixousSelfHosted", result.Provider);
        Assert.Contains("\"source\":\"en\"", handler.LastBody);
        Assert.Contains("\"target\":\"ta\"", handler.LastBody);
        Assert.Contains("\"items\":[\"Welcome to our website.\"]", handler.LastBody);
    }

    [Theory]
    [InlineData("""{"translations":["x"],"quality":{"checked":true,"script_anomaly_items":[0]}}""", true)]
    [InlineData("""{"translations":["x"],"quality":{"checked":true,"script_anomaly_items":[]}}""", false)]
    [InlineData("""{"translations":["x"],"quality":{"checked":false,"script_anomaly_items":[]}}""", null)]
    [InlineData("""{"translations":["x"]}""", null)]
    [InlineData("""{"translations":["x"],"quality":"garbage"}""", null)]
    public async Task Translate_MapsSidecarScriptAnomaly_NullWhenNotChecked(string body, bool? expected)
    {
        var handler = new StubHandler { Body = body };
        var result = await NewProvider(handler).TranslateAsync("Hello.", "ta", "en");
        Assert.Equal(expected, result.ScriptAnomaly);
    }

    [Fact]
    public async Task Translate_SendsBearerToken_WhenConfigured()
    {
        var handler = new StubHandler();
        var provider = NewProvider(handler, token: "test-token");
        await provider.TranslateAsync("Hello.", "ta", "en");
        Assert.Equal("test-token", handler.SeenToken);
    }

    [Fact]
    public async Task Translate_SameLanguage_ReturnsInputWithoutHttp()
    {
        var handler = new StubHandler();
        var provider = NewProvider(handler);
        var result = await provider.TranslateAsync("Already Tamil target.", "en", "en");
        Assert.Equal("Already Tamil target.", result.TranslatedText);
        Assert.Null(handler.LastBody);
    }

    [Theory]
    [InlineData("""{"detail":"item exceeds 2000 chars"}""")]
    [InlineData("<html><body><h1>413</h1>Your text: SECRET-REFLECTED-INPUT</body></html>")]
    [InlineData("""{"detail":"/srv/engine/models/internal-path token=sk-live-123 SECRET-REFLECTED-INPUT"}""")]
    [InlineData("")]
    public async Task Translate_Sidecar413_IsFixedWordingValidationError_NoUpstreamBodyForwarded(string upstreamBody)
    {
        var handler = new StubHandler { Status = HttpStatusCode.RequestEntityTooLarge, Body = upstreamBody };
        var provider = NewProvider(handler);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => provider.TranslateAsync("Hello.", "ta", "en"));
        Assert.Equal(PixousSelfHostedTranslationProvider.SizeLimitMessage, ex.Message);
        Assert.DoesNotContain("SECRET-REFLECTED-INPUT", ex.ToString());
        Assert.DoesNotContain("sk-live", ex.ToString());
        Assert.DoesNotContain("internal-path", ex.ToString());
        Assert.Contains("413", ex.InnerException!.Message);
    }

    [Fact]
    public async Task Translate_Sidecar413_HugeBody_IsNotForwarded()
    {
        var handler = new StubHandler { Status = HttpStatusCode.RequestEntityTooLarge, Body = new string('A', 500_000) };
        var provider = NewProvider(handler);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => provider.TranslateAsync("Hello.", "ta", "en"));
        Assert.Equal(PixousSelfHostedTranslationProvider.SizeLimitMessage, ex.Message);
        Assert.True(ex.ToString().Length < 4000);
    }

    [Fact]
    public async Task Translate_Sidecar503ResourceLimit_IsTypedCapacityError_NoBodyForwarded()
    {
        var handler = new StubHandler { Status = HttpStatusCode.ServiceUnavailable,
            Body = """{"detail":{"code":"resource_limit","message":"SECRET-REFLECTED-INPUT /srv/internal"}}""" };
        var provider = NewProvider(handler);
        var ex = await Assert.ThrowsAsync<TranslationCapacityException>(() => provider.TranslateAsync("Hello.", "ta", "en"));
        Assert.False(ex.Busy);
        Assert.DoesNotContain("SECRET-REFLECTED-INPUT", ex.ToString());
        Assert.DoesNotContain("internal", ex.ToString());
    }

    [Fact]
    public async Task Translate_Sidecar503Plain_StaysGenericUpstreamError_NoBodyInMessage()
    {
        var handler = new StubHandler { Status = HttpStatusCode.ServiceUnavailable, Body = "SECRET-REFLECTED-INPUT" };
        var provider = NewProvider(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranslateAsync("Hello.", "ta", "en"));
        Assert.IsNotType<TranslationCapacityException>(ex);
        Assert.DoesNotContain("SECRET-REFLECTED-INPUT", ex.ToString());
    }

    [Fact]
    public async Task Translate_Sidecar429_IsTypedBusy_NoBodyForwarded()
    {
        var handler = new StubHandler { Status = (HttpStatusCode)429, Body = """{"detail":"SECRET-REFLECTED-INPUT"}""" };
        var provider = NewProvider(handler);
        var ex = await Assert.ThrowsAsync<TranslationCapacityException>(() => provider.TranslateAsync("Hello.", "ta", "en"));
        Assert.True(ex.Busy);
        Assert.DoesNotContain("SECRET-REFLECTED-INPUT", ex.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData((HttpStatusCode)422)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Translate_SidecarAuthOrServerFailure_StaysUpstreamError(HttpStatusCode status)
    {
        var handler = new StubHandler { Status = status, Body = """{"detail":"x"}""" };
        var provider = NewProvider(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranslateAsync("Hello.", "ta", "en"));
    }

    [Fact]
    public async Task Translate_ThrowsClearError_OnSidecarFailure()
    {
        var handler = new StubHandler { Status = HttpStatusCode.BadRequest, Body = """{"detail":"unsupported language pair"}""" };
        var provider = NewProvider(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.TranslateAsync("Hello.", "zz", "en"));
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task Translate_WithoutSource_UsesScriptDetection()
    {
        var handler = new StubHandler();
        var provider = NewProvider(handler);
        await provider.TranslateAsync("வணக்கம்", "en");
        Assert.Contains("\"source\":\"ta\"", handler.LastBody);
    }

    [Fact]
    public async Task Translate_UndetectableSource_ThrowsWithoutHttp()
    {
        var handler = new StubHandler();
        var provider = NewProvider(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranslateAsync("12345 !!!", "ta"));
        Assert.Null(handler.LastBody);
    }

    [Theory]
    [InlineData("வணக்கம், நீங்கள் எப்படி இருக்கிறீர்கள்?", "ta")]
    [InlineData("नमस्ते, आप कैसे हैं?", "hi")]
    [InlineData("హలో, మీరు ఎలా ఉన్నారు?", "te")]
    [InlineData("ഹലോ, നിങ്ങൾക്ക് സുഖമാണോ?", "ml")]
    [InlineData("ನಮಸ್ಕಾರ, ನೀವು ಹೇಗಿದ್ದೀರಿ?", "kn")]
    [InlineData("Hello, how are you?", "en")]
    public async Task Detect_IdentifiesPriorityScripts(string text, string expected)
    {
        var provider = NewProvider(new StubHandler());
        var detected = await provider.DetectAsync(text);
        Assert.Equal(expected, detected.Language);
        Assert.True(detected.Confidence > 0.5);
    }

    [Fact]
    public async Task Detect_ReturnsUnd_ForScriptlessInput()
    {
        var provider = NewProvider(new StubHandler());
        var detected = await provider.DetectAsync("12345 !!!");
        Assert.Equal("und", detected.Language);
    }

    [Fact]
    public async Task MissingEndpoint_ThrowsConfigError()
    {
        var provider = new PixousSelfHostedTranslationProvider(
            new HttpClient(new StubHandler()),
            new PixousEngineOptions(),
            new ResilienceOptions(),
            null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranslateAsync("Hello.", "ta", "en"));
        Assert.Contains("PixousEngine:Endpoint", ex.Message);
    }

    [Fact]
    public async Task SupportedLanguages_CoversSixPriorityPlusScheduled()
    {
        var provider = NewProvider(new StubHandler());
        var langs = await provider.GetSupportedLanguagesAsync();
        foreach (var code in new[] { "en", "ta", "hi", "te", "ml", "kn" })
            Assert.Contains(langs, l => l.Code == code);
        Assert.Equal(23, langs.Count);
    }
}
