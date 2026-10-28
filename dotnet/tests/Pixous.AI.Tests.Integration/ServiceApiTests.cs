using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// The shared /api/ai/v1 surface: X-Api-Key auth, per-key rate limiting,
/// key lifecycle through the password-gated settings endpoints.
/// </summary>
public class ServiceApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ServiceApiTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    private async Task<string> LoginAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = "pixous" });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private async Task<string> CreateKeyAsync(string token, string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/settings/service-keys");
        req.Headers.Add("X-Settings-Token", token);
        req.Content = JsonContent.Create(new { name });
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var raw = doc.RootElement.GetProperty("data").GetProperty("apiKey").GetString()!;
        Assert.StartsWith("pk_", raw);
        return raw;
    }

    private HttpRequestMessage V1(HttpMethod method, string path, string? apiKey = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, "/api/ai/v1" + path);
        if (apiKey is not null) req.Headers.Add("X-Api-Key", apiKey);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task Chat_Without_Key_Is_401_With_Envelope()
    {
        var res = await _client.SendAsync(V1(HttpMethod.Post, "/chat", body: new { message = "hi" }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("SERVICE_KEY_INVALID", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Chat_With_Key_Returns_Reply_And_Counts_Usage()
    {
        var token = await LoginAsync();
        var key = await CreateKeyAsync(token, "itest-usage-" + Guid.NewGuid().ToString("n")[..6]);

        var res = await _client.SendAsync(V1(HttpMethod.Post, "/chat", key, new { message = "hello", sessionId = "s1" }));
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/ai/settings/service-keys");
        listReq.Headers.Add("X-Settings-Token", token);
        using var listDoc = JsonDocument.Parse(await (await _client.SendAsync(listReq)).Content.ReadAsStringAsync());
        var entry = listDoc.RootElement.GetProperty("data").EnumerateArray()
            .First(k => k.GetProperty("prefix").GetString() == key[..10]);
        Assert.True(entry.GetProperty("totalRequests").GetInt64() >= 1);
    }

    [Fact]
    public async Task Revoke_And_Rotate_Take_Effect_Immediately()
    {
        var token = await LoginAsync();
        var key = await CreateKeyAsync(token, "itest-lifecycle-" + Guid.NewGuid().ToString("n")[..6]);

        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/ai/settings/service-keys");
        listReq.Headers.Add("X-Settings-Token", token);
        using var listDoc = JsonDocument.Parse(await (await _client.SendAsync(listReq)).Content.ReadAsStringAsync());
        var id = listDoc.RootElement.GetProperty("data").EnumerateArray()
            .First(k => k.GetProperty("prefix").GetString() == key[..10])
            .GetProperty("id").GetString()!;

        var revokeReq = new HttpRequestMessage(HttpMethod.Post, $"/api/ai/settings/service-keys/{id}/revoke");
        revokeReq.Headers.Add("X-Settings-Token", token);
        (await _client.SendAsync(revokeReq)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.SendAsync(V1(HttpMethod.Post, "/chat", key, new { message = "hi" }))).StatusCode);

        var rotateReq = new HttpRequestMessage(HttpMethod.Post, $"/api/ai/settings/service-keys/{id}/rotate");
        rotateReq.Headers.Add("X-Settings-Token", token);
        using var rotDoc = JsonDocument.Parse(await (await _client.SendAsync(rotateReq)).Content.ReadAsStringAsync());
        var newKey = rotDoc.RootElement.GetProperty("data").GetProperty("apiKey").GetString()!;
        Assert.NotEqual(key, newKey);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.SendAsync(V1(HttpMethod.Post, "/chat", key, new { message = "hi" }))).StatusCode);
        (await _client.SendAsync(V1(HttpMethod.Post, "/chat", newKey, new { message = "hi" }))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ServiceKey_Management_Requires_Settings_Token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/ai/settings/service-keys", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/ai/settings/service-keys")).StatusCode);
    }

    [Fact]
    public async Task Per_Key_Rate_Limit_Returns_429_With_RetryAfter()
    {
        var token = await LoginAsync();
        var key = await CreateKeyAsync(token, "itest-ratelimit-" + Guid.NewGuid().ToString("n")[..6]);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 61; i++)
        {
            last?.Dispose();
            last = await _client.SendAsync(V1(HttpMethod.Get, "/languages", key));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("60", last.Headers.RetryAfter?.ToString());
        using var doc = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
        Assert.Equal("SERVICE_KEY_RATE_LIMITED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Session_Isolation_Survives_Adversarial_SessionIds()
    {
        var token = await LoginAsync();
        var keyA = await CreateKeyAsync(token, "itest-iso-a-" + Guid.NewGuid().ToString("n")[..6]);
        var keyB = await CreateKeyAsync(token, "itest-iso-b-" + Guid.NewGuid().ToString("n")[..6]);

        // Resolve each key's id via the admin list.
        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/ai/settings/service-keys");
        listReq.Headers.Add("X-Settings-Token", token);
        using var listDoc = JsonDocument.Parse(await (await _client.SendAsync(listReq)).Content.ReadAsStringAsync());
        var entries = listDoc.RootElement.GetProperty("data").EnumerateArray().ToArray();
        var idA = entries.First(k => k.GetProperty("prefix").GetString() == keyA[..10]).GetProperty("id").GetString()!;
        var idB = entries.First(k => k.GetProperty("prefix").GetString() == keyB[..10]).GetProperty("id").GetString()!;

        // Key A establishes session "s1" (scoped to A internally).
        var resA = await _client.SendAsync(V1(HttpMethod.Post, "/chat", keyA, new { message = "hello", sessionId = "s1" }));
        using var docA = JsonDocument.Parse(await resA.Content.ReadAsStringAsync());
        Assert.Equal(idA + ":s1", docA.RootElement.GetProperty("data").GetProperty("sessionId").GetString());

        // ADVERSARIAL: key B tries to read A's session by replaying A's scoped id verbatim.
        var resB = await _client.SendAsync(V1(HttpMethod.Post, "/chat", keyB,
            new { message = "hello", sessionId = idA + ":s1" }));
        using var docB = JsonDocument.Parse(await resB.Content.ReadAsStringAsync());
        // The service re-scopes under B — B can never land in A's namespace.
        Assert.Equal(idB + ":" + idA + ":s1", docB.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
    }

        [Fact]
    public async Task Streaming_Chat_With_Key_Emits_Sse()
    {
        var token = await LoginAsync();
        var key = await CreateKeyAsync(token, "itest-sse-" + Guid.NewGuid().ToString("n")[..6]);
        var res = await _client.SendAsync(V1(HttpMethod.Post, "/chat/stream", key, new { message = "hello" }));
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("data: ", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }
}
