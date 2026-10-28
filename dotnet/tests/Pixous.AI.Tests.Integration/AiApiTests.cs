using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// Full-stack tests over HTTP (no mocks, no real AI keys — Echo/KnowledgeBase).
/// Asserts the standard envelope { success, data, error{ code, message }, traceId }.
/// </summary>
public class AiApiTests : IClassFixture<AiApiTests.Factory>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Test host pinned to the offline Echo translation provider; the sample's
    /// appsettings default is the self-hosted engine sidecar, which is not
    /// running under test. Engine-backed translation is covered separately.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AiSuite:TranslationProvider", "Echo");
        }
    }

    public AiApiTests(Factory factory)
    {
        _client = factory.CreateClient();
    }

    private static async Task<JsonDocument> EnvelopeAsync(HttpResponseMessage res)
    {
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private async Task<string> LoginAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = "pixous" });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private async Task<HttpResponseMessage> PostSelectionAsync(object body, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/selection");
        if (token is not null) req.Headers.Add("X-Settings-Token", token);
        req.Content = JsonContent.Create(body);
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task Selection_RequiresSettingsToken()
    {
        var res = await PostSelectionAsync(new { chatProvider = "Echo", translationProvider = (string?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Chat_ReturnsEnvelope_WithReply()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat",
            new { message = "what are your services?" });
        using var doc = await EnvelopeAsync(res);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Pixous", doc.RootElement.GetProperty("data").GetProperty("reply").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Chat_EmptyMessage_ReturnsValidationEnvelope()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Translate_ReturnsEnvelope()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/translation/translate",
            new { text = "Good morning", targetLanguage = "ta" });
        using var doc = await EnvelopeAsync(res);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("[ta]", doc.RootElement.GetProperty("data").GetProperty("translatedText").GetString());
    }

    [Fact]
    public async Task TranslateBatch_ReturnsAll()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/translation/batch",
            new { texts = new[] { "hi", "bye" }, targetLanguage = "es" });
        using var doc = await EnvelopeAsync(res);
        Assert.Equal(2, doc.RootElement.GetProperty("data").GetProperty("translatedTexts").GetArrayLength());
    }

    [Fact]
    public async Task DetectLanguage_ReturnsEnvelope()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/translation/detect-language",
            new { text = "hello" });
        using var doc = await EnvelopeAsync(res);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Echo", doc.RootElement.GetProperty("data").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Languages_ListsSupported()
    {
        var res = await _client.GetAsync("/api/ai/translation/languages");
        using var doc = await EnvelopeAsync(res);
        Assert.True(doc.RootElement.GetProperty("data").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Providers_ListsRegistered()
    {
        var res = await _client.GetAsync("/api/ai/providers");
        using var doc = await EnvelopeAsync(res);
        var chats = doc.RootElement.GetProperty("data").GetProperty("chatProviders")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("KnowledgeBase", chats);
        Assert.Contains("Echo", chats);
    }

    [Fact]
    public async Task Info_ExposesServices_WithoutSecrets()
    {
        var res = await _client.GetAsync("/api/ai/info");
        using var doc = await EnvelopeAsync(res);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("services").GetArrayLength());
        var raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-", raw);
    }

    [Fact]
    public async Task Selection_SwitchAndReset_Works()
    {
        // Switch to Echo.
        var token = await LoginAsync();
        var set = await PostSelectionAsync(new { chatProvider = "Echo", translationProvider = (string?)null }, token);
        set.EnsureSuccessStatusCode();

        var chat = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "hello" });
        using var doc = JsonDocument.Parse(await chat.Content.ReadAsStringAsync());
        Assert.Contains("[Echo]", doc.RootElement.GetProperty("data").GetProperty("reply").GetString());

        // Unknown provider is rejected.
        var bad = await PostSelectionAsync(new { chatProvider = "Nope" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Reset to configured defaults (self-contained: leaves no global state behind).
        var reset = await PostSelectionAsync(new { chatProvider = (string?)null, translationProvider = (string?)null }, token);
        reset.EnsureSuccessStatusCode();

        var chat2 = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "hi" });
        using var doc2 = JsonDocument.Parse(await chat2.Content.ReadAsStringAsync());
        Assert.Contains("Pixous Assistant", doc2.RootElement.GetProperty("data").GetProperty("reply").GetString());
    }

    [Fact]
    public async Task ChatStream_EndsWithDone()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat/stream", new { message = "hi" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", body);
    }

    [Fact]
    public async Task Chat_WithHistory_SerializesStringRoles()
    {
        // Real clients (demo page, Angular library, any REST caller) send roles
        // as strings: { "role": "user" }. A non-empty history must not 400.
        var res = await _client.PostAsJsonAsync("/api/ai/chat", new
        {
            message = "what about products?",
            history = new object[]
            {
                new { role = "user", content = "hi" },
                new { role = "assistant", content = "Hello!" },
            },
        });
        using var doc = await EnvelopeAsync(res); // throws on 400
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            doc.RootElement.GetProperty("data").GetProperty("reply").GetString()));
    }

    [Fact]
    public async Task ChatStream_ValidationError_EmitsExplicitErrorFrame_NotBlankDone()
    {
        // Over the 4000-char limit: the stream must surface an explicit
        // {"error"} frame (400 before the body starts), never a silent blank + [DONE].
        var res = await _client.PostAsJsonAsync("/api/ai/chat/stream", new { message = new string('x', 4001) });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("{\"error\"", body);
        Assert.Contains("\"code\":\"VALIDATION\"", body);
        Assert.DoesNotContain("data: [DONE]", body);
    }

    [Fact]
    public async Task ChatStream_WithHistory_SerializesStringRoles()
    {
        // Same contract over SSE — this is the exact shape the demo page posts.
        var res = await _client.PostAsJsonAsync("/api/ai/chat/stream", new
        {
            message = "services",
            history = new object[] { new { role = "user", content = "hi" } },
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", body);
        Assert.DoesNotContain("{\"error\"", body);
    }
}
