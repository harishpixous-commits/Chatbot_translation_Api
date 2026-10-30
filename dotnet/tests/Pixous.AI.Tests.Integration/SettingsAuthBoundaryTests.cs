using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// Every admin route must reject a caller with no / a bogus session token (401),
/// and repeated wrong passwords must lock the caller out even for the right password.
/// </summary>
public class SettingsAuthBoundaryTests : IClassFixture<SettingsAuthBoundaryTests.Factory>
{
    private readonly HttpClient _client;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("AiSuite:TranslationProvider", "Echo");
    }

    public SettingsAuthBoundaryTests(Factory f) => _client = f.CreateClient();

    public static IEnumerable<object[]> AdminRoutes() => new[]
    {
        new object[] { "GET",    "/api/ai/settings" },
        new object[] { "PUT",    "/api/ai/settings/password" },
        new object[] { "POST",   "/api/ai/settings/llm-keys" },
        new object[] { "PUT",    "/api/ai/settings/llm-keys/x" },
        new object[] { "DELETE", "/api/ai/settings/llm-keys/x" },
        new object[] { "POST",   "/api/ai/settings/llm-keys/x/test" },
        new object[] { "PUT",    "/api/ai/settings/voice" },
        new object[] { "POST",   "/api/ai/settings/kb" },
        new object[] { "DELETE", "/api/ai/settings/kb/x" },
        new object[] { "POST",   "/api/ai/settings/service-keys" },
        new object[] { "GET",    "/api/ai/settings/service-keys" },
        new object[] { "POST",   "/api/ai/settings/service-keys/x/revoke" },
        new object[] { "POST",   "/api/ai/settings/service-keys/x/rotate" },
        new object[] { "POST",   "/api/ai/selection" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_NoToken_Or_BogusToken_Is401(string method, string url)
    {
        foreach (var token in new string?[] { null, "deadbeef", "Bearer-less" })
        {
            var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (token is not null) req.Headers.Add("X-Settings-Token", token);
            if (method != "GET" && method != "DELETE") req.Content = JsonContent.Create(new { });
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task Login_Lockout_AfterRepeatedFailures_BlocksEvenCorrectPassword()
    {
        for (var i = 0; i < 5; i++)
        {
            var bad = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = "wrong-" + i });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        var good = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = "pixous" });
        Assert.Equal(HttpStatusCode.Unauthorized, good.StatusCode);
        using var doc = JsonDocument.Parse(await good.Content.ReadAsStringAsync());
        Assert.Contains("Too many failed attempts", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
