using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// Full-stack tests for the password-gated Settings surface: login/token,
/// LLM key CRUD (masked), voice config, knowledge uploads + retrieval, STT/TTS
/// runtime. Each factory uses an isolated temp store and fast resilience.
/// </summary>
public class SettingsApiTests : IClassFixture<SettingsApiTests.Factory>
{
    private readonly HttpClient _client;
    private readonly Factory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string StoreDir { get; } =
            Path.Combine(Path.GetTempPath(), "pixous-settings-it-" + Guid.NewGuid().ToString("n"));
        public const string Password = "it-pass-1234";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AiSuite:Settings:StoreDirectory", StoreDir);
            builder.UseSetting("AiSuite:Settings:Password", Password);
            builder.UseSetting("AiSuite:Resilience:MaxRetries", "0");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (Directory.Exists(StoreDir)) Directory.Delete(StoreDir, true); }
            catch { /* best effort */ }
        }
    }

    public SettingsApiTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        public string StoreDir { get; } =
            Path.Combine(Path.GetTempPath(), "pixous-settings-prod-" + Guid.NewGuid().ToString("n"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("AiSuite:Settings:StoreDirectory", StoreDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (Directory.Exists(StoreDir)) Directory.Delete(StoreDir, true); }
            catch { /* best effort */ }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────
    private async Task<string> LoginAsync(string? password = null)
    {
        var res = await _client.PostAsJsonAsync("/api/ai/settings/login",
            new { password = password ?? Factory.Password });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private HttpRequestMessage SettingsReq(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Settings-Token", token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    // ── auth ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Login_WrongPassword_ThenRight_Works_AndLockoutClears()
    {
        var wrong = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var wrongBody = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHORIZED", wrongBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Incorrect", wrongBody.RootElement.GetProperty("error").GetProperty("message").GetString());

        // Correct login must still succeed (and resets the failure counter).
        var token = await LoginAsync();
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task Settings_WithoutToken_401_WithToken_200()
    {
        var anon = await _client.GetAsync("/api/ai/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var token = await LoginAsync();
        var res = await _client.SendAsync(SettingsReq(HttpMethod.Get, "/api/ai/settings", token));
        res.EnsureSuccessStatusCode();
        var data = await DataAsync(res);

        Assert.True(data.GetProperty("security").GetProperty("persistKeys").GetBoolean());
        // Password may live in config or (after a change) the store — either is valid.
        Assert.Contains(data.GetProperty("security").GetProperty("passwordSource").GetString()!,
            new[] { "config", "stored" });
        Assert.True(data.GetProperty("security").GetProperty("minPasswordLength").GetInt32() >= 4);
        Assert.True(data.GetProperty("vendorPresets").GetArrayLength() >= 5);
        Assert.Empty(data.GetProperty("llmKeys").EnumerateArray());
        // Snapshot must never contain raw keys or the raw password.
        Assert.DoesNotContain(Factory.Password, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BearerToken_AlsAccepted()
    {
        var token = await LoginAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/ai/settings");
        req.Headers.Authorization = new("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Logout_InvalidatesToken()
    {
        var token = await LoginAsync();
        var logout = await _client.SendAsync(SettingsReq(HttpMethod.Post, "/api/ai/settings/logout", token));
        logout.EnsureSuccessStatusCode();
        var after = await _client.SendAsync(SettingsReq(HttpMethod.Get, "/api/ai/settings", token));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // ── LLM keys ─────────────────────────────────────────────────────────
    [Fact]
    public async Task LlmKey_Crud_Masking_OrderDelete()
    {
        var token = await LoginAsync();

        // Add two keys — responses must carry masked keys only.
        var add1 = await _client.SendAsync(SettingsReq(HttpMethod.Post, "/api/ai/settings/llm-keys", token, new
        {
            name = "it-primary",
            vendor = "OpenAI",
            apiKey = "sk-integration-key-alpha1234"
        }));
        add1.EnsureSuccessStatusCode();
        var k1 = await DataAsync(add1);
        var id1 = k1.GetProperty("id").GetString()!;
        Assert.Equal("sk-i…1234", k1.GetProperty("apiKeyMasked").GetString());
        Assert.True(k1.GetProperty("hasKey").GetBoolean());
        Assert.Equal("https://api.openai.com/v1", k1.GetProperty("endpoint").GetString()); // preset default
        var raw1 = await add1.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-integration-key-alpha1234", raw1); // never echoed

        var add2 = await _client.SendAsync(SettingsReq(HttpMethod.Post, "/api/ai/settings/llm-keys", token, new
        {
            name = "it-backup",
            vendor = "Anthropic",
            apiKey = "sk-ant-integration-key-beta999"
        }));
        add2.EnsureSuccessStatusCode();
        var k2 = await DataAsync(add2);
        var id2 = k2.GetProperty("id").GetString()!;
        Assert.Equal("https://api.anthropic.com", k2.GetProperty("endpoint").GetString());

        // Snapshot: both listed, masked, ordered.
        var snap = await DataAsync(await _client.SendAsync(
            SettingsReq(HttpMethod.Get, "/api/ai/settings", token)));
        var keys = snap.GetProperty("llmKeys").EnumerateArray().ToList();
        Assert.Equal(2, keys.Count);
        Assert.DoesNotContain(keys, k => k.GetProperty("name").GetString()!.StartsWith("it-") &&
            k.GetProperty("apiKeyMasked").GetString()!.Length == 0);

        // Move backup up → becomes first.
        var move = await _client.SendAsync(SettingsReq(HttpMethod.Put,
            $"/api/ai/settings/llm-keys/{id2}", token, new { move = "up" }));
        move.EnsureSuccessStatusCode();
        snap = await DataAsync(await _client.SendAsync(
            SettingsReq(HttpMethod.Get, "/api/ai/settings", token)));
        keys = snap.GetProperty("llmKeys").EnumerateArray().ToList();
        Assert.Equal("it-backup", keys[0].GetProperty("name").GetString());

        // Validation: endpoint must be an absolute URL.
        var bad = await _client.SendAsync(SettingsReq(HttpMethod.Put,
            $"/api/ai/settings/llm-keys/{id1}", token, new { endpoint = "not-a-url" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Update model + delete both (leave the store clean).
        var ok = await _client.SendAsync(SettingsReq(HttpMethod.Put,
            $"/api/ai/settings/llm-keys/{id1}", token, new { model = "gpt-4o" }));
        ok.EnsureSuccessStatusCode();
        Assert.Equal("gpt-4o", (await DataAsync(ok)).GetProperty("model").GetString());

        (await _client.SendAsync(SettingsReq(HttpMethod.Delete, $"/api/ai/settings/llm-keys/{id1}", token)))
            .EnsureSuccessStatusCode();
        (await _client.SendAsync(SettingsReq(HttpMethod.Delete, $"/api/ai/settings/llm-keys/{id2}", token)))
            .EnsureSuccessStatusCode();

        snap = await DataAsync(await _client.SendAsync(
            SettingsReq(HttpMethod.Get, "/api/ai/settings", token)));
        Assert.DoesNotContain(snap.GetProperty("llmKeys").EnumerateArray(),
            k => k.GetProperty("name").GetString()!.StartsWith("it-"));
    }

    [Fact]
    public async Task LlmKey_Add_MissingKey_Rejected()
    {
        var token = await LoginAsync();
        var res = await _client.SendAsync(SettingsReq(HttpMethod.Post, "/api/ai/settings/llm-keys", token, new
        {
            name = "no-key",
            vendor = "OpenAI",
            apiKey = ""
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("API key", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task LlmKey_Test_UnreachableEndpoint_ReturnsOkFalse_NoLeak()
    {
        var token = await LoginAsync();
        var add = await _client.SendAsync(SettingsReq(HttpMethod.Post, "/api/ai/settings/llm-keys", token, new
        {
            name = "it-unreachable",
            vendor = "Custom",
            endpoint = "http://127.0.0.1:9/v1",
            model = "test-model",
            apiKey = "sk-should-not-leak-abcdef"
        }));
        add.EnsureSuccessStatusCode();
        var id = (await DataAsync(add)).GetProperty("id").GetString()!;

        var test = await _client.SendAsync(SettingsReq(HttpMethod.Post,
            $"/api/ai/settings/llm-keys/{id}/test", token));
        test.EnsureSuccessStatusCode();
        var data = await DataAsync(test);
        Assert.False(data.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("message").GetString()));

        await _client.SendAsync(SettingsReq(HttpMethod.Delete, $"/api/ai/settings/llm-keys/{id}", token));
    }

    // ── password ─────────────────────────────────────────────────────────
    [Fact]
    public async Task ChangePassword_WrongCurrent_401_NewWorks_Restore()
    {
        var token = await LoginAsync();

        var wrong = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/password", token,
            new { currentPassword = "bad", newPassword = "brand-new-pass-99" }));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var change = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/password", token,
            new { currentPassword = Factory.Password, newPassword = "brand-new-pass-99" }));
        change.EnsureSuccessStatusCode();

        // Old password rejected, new accepted.
        var oldLogin = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = Factory.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newToken = await LoginAsync("brand-new-pass-99");
        Assert.False(string.IsNullOrWhiteSpace(newToken));

        // Restore the original so other tests keep working.
        var restore = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/password", newToken,
            new { currentPassword = "brand-new-pass-99", newPassword = Factory.Password }));
        restore.EnsureSuccessStatusCode();
        var restoreCheck = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = Factory.Password });
        restoreCheck.EnsureSuccessStatusCode();
    }

    // ── voice config + runtime ───────────────────────────────────────────
    [Fact]
    public async Task Voice_ConfigSave_Masking_Validation_Runtime()
    {
        var token = await LoginAsync();

        // STT disabled by default → runtime call fails with actionable error.
        var disabled = await _client.PostAsync("/api/ai/voice/stt",
            new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "a.wav" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);

        // Save Echo voice config.
        var save = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/voice", token, new
        {
            sttEnabled = true,
            sttProvider = "Echo",
            ttsEnabled = true,
            ttsProvider = "Echo"
        }));
        save.EnsureSuccessStatusCode();
        var voice = (await DataAsync(save)).GetProperty("voice");
        Assert.True(voice.GetProperty("sttEnabled").GetBoolean());
        Assert.Equal("Echo", voice.GetProperty("sttProvider").GetString());

        // Store a voice API key …
        var withKey = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/voice", token, new
        {
            sttEnabled = true, sttProvider = "Echo", sttApiKey = "sk-voice-secret-123456",
            ttsEnabled = true, ttsProvider = "Echo"
        }));
        withKey.EnsureSuccessStatusCode();
        var vk = (await DataAsync(withKey)).GetProperty("voice");
        Assert.True(vk.GetProperty("sttHasKey").GetBoolean());
        Assert.Equal("sk-v…3456", vk.GetProperty("sttKeyMasked").GetString());

        // … then save again with a blank key field: the secret must SURVIVE
        // (the UI can never round-trip a masked key).
        var blank = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/voice", token, new
        {
            sttEnabled = true, sttProvider = "Echo", sttApiKey = "",
            ttsEnabled = true, ttsProvider = "Echo"
        }));
        blank.EnsureSuccessStatusCode();
        var afterBlank = await blank.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-voice-secret-123456", afterBlank);
        Assert.True((await DataAsync(blank)).GetProperty("voice")
            .GetProperty("sttHasKey").GetBoolean());

        // Runtime STT + TTS now work (keyless demo providers).
        var stt = await _client.PostAsync("/api/ai/voice/stt",
            new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "a.wav" } });
        stt.EnsureSuccessStatusCode();
        var sttData = await DataAsync(stt);
        Assert.Contains("demo speech-to-text", sttData.GetProperty("text").GetString());

        var tts = await _client.PostAsJsonAsync("/api/ai/voice/tts", new { text = "hello from voice" });
        tts.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", tts.Content.Headers.ContentType!.MediaType);
        Assert.True((await tts.Content.ReadAsByteArrayAsync()).Length > 44);

        // Validation: unknown provider rejected.
        var bad = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/voice", token,
            new { sttEnabled = true, sttProvider = "Skynet", ttsEnabled = false, ttsProvider = "Browser" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Restore disabled voice so other tests see the default.
        var off = await _client.SendAsync(SettingsReq(HttpMethod.Put, "/api/ai/settings/voice", token,
            new { sttEnabled = false, sttProvider = "Browser", ttsEnabled = false, ttsProvider = "Browser" }));
        off.EnsureSuccessStatusCode();
    }

    // ── knowledge base uploads ───────────────────────────────────────────
    [Fact]
    public async Task Kb_Upload_Find_Delete_Lifecycle()
    {
        var token = await LoginAsync();

        // Upload a markdown document.
        const string content = """
            # Return Policy Handbook

            Our return policy allows customers to return any product within thirty days
            of purchase with a full refund. Shipping costs are not refundable.
            """;
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", "returns.md");
        var upload = await _client.SendAsync(
            MultipartReq(HttpMethod.Post, "/api/ai/settings/kb", token, form));
        upload.EnsureSuccessStatusCode();
        var data = await DataAsync(upload);
        var docId = data.GetProperty("document").GetProperty("id").GetString()!;
        Assert.True(data.GetProperty("document").GetProperty("chunkCount").GetInt32() >= 1);
        Assert.Equal(0, data.GetProperty("warnings").GetArrayLength());

        // Snapshot lists it.
        var snap = await DataAsync(await _client.SendAsync(
            SettingsReq(HttpMethod.Get, "/api/ai/settings", token)));
        Assert.Contains(snap.GetProperty("documents").EnumerateArray(),
            d => d.GetProperty("id").GetString() == docId);

        // The KnowledgeBase chat engine answers from the uploaded document.
        var chat = await _client.PostAsJsonAsync("/api/ai/chat",
            new { message = "what is your return policy?" });
        chat.EnsureSuccessStatusCode();
        var reply = (await DataAsync(chat)).GetProperty("reply").GetString()!;
        Assert.Contains("Return Policy Handbook", reply);
        Assert.Contains("thirty days", reply);

        // Upload unsupported type → validation error.
        var form2 = new MultipartFormDataContent();
        form2.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("slides")), "file", "deck.pptx");
        var bad = await _client.SendAsync(
            MultipartReq(HttpMethod.Post, "/api/ai/settings/kb", token, form2));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Delete → the FAQ fallback answers again.
        var del = await _client.SendAsync(SettingsReq(HttpMethod.Delete, $"/api/ai/settings/kb/{docId}", token));
        del.EnsureSuccessStatusCode();

        var chat2 = await _client.PostAsJsonAsync("/api/ai/chat",
            new { message = "what is your return policy?" });
        var reply2 = (await DataAsync(chat2)).GetProperty("reply").GetString()!;
        Assert.DoesNotContain("Return Policy Handbook", reply2);
    }

    private static HttpRequestMessage MultipartReq(HttpMethod method, string url, string token,
        MultipartFormDataContent form)
    {
        var req = new HttpRequestMessage(method, url) { Content = form };
        req.Headers.Add("X-Settings-Token", token);
        return req;
    }

    // ── info contract ────────────────────────────────────────────────────
    [Fact]
    public async Task Info_ExposesSettingsAndVoiceFlags_WithoutSecrets()
    {
        var res = await _client.GetAsync("/api/ai/info");
        res.EnsureSuccessStatusCode();
        var raw = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        Assert.True(data.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.True(data.GetProperty("llmKeysConfigured").GetInt32() >= 0);
        Assert.True(data.GetProperty("voice").TryGetProperty("sttEnabled", out _));
        Assert.Equal(2, data.GetProperty("services").GetArrayLength()); // unchanged contract
        Assert.DoesNotContain("sk-", raw);
        Assert.DoesNotContain("it-pass", raw);
    }

    [Fact]
    public async Task MultiLLM_Registered_AndSelectionWorks()
    {
        var res = await _client.GetAsync("/api/ai/providers");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var chats = doc.RootElement.GetProperty("data").GetProperty("chatProviders")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("MultiLLM", chats);

        // Select MultiLLM with no keys → clear, actionable error; then restore.
        var selToken = await LoginAsync();
        var selReq = new HttpRequestMessage(HttpMethod.Post, "/api/ai/selection")
        { Content = JsonContent.Create(new { chatProvider = "MultiLLM", translationProvider = (string?)null }) };
        selReq.Headers.Add("X-Settings-Token", selToken);
        var set = await _client.SendAsync(selReq);
        set.EnsureSuccessStatusCode();

        var chat = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "hello" });
        var body = JsonDocument.Parse(await chat.Content.ReadAsStringAsync());
        // Fail() maps InvalidOperationException → ProblemDetails (title), not the envelope.
        var msg = body.RootElement.TryGetProperty("title", out var title)
            ? title.GetString() ?? ""
            : body.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        Assert.Contains("Settings", msg);
        Assert.Contains("LLM APIs", msg);

        var resetReq = new HttpRequestMessage(HttpMethod.Post, "/api/ai/selection")
        { Content = JsonContent.Create(new { chatProvider = (string?)null, translationProvider = (string?)null }) };
        resetReq.Headers.Add("X-Settings-Token", selToken);
        (await _client.SendAsync(resetReq)).EnsureSuccessStatusCode();
    }
}
