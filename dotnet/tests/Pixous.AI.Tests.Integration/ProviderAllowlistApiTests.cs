using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>HTTP-level proof that AllowedClientProviders rejects a per-request provider not on the list.</summary>
public class ProviderAllowlistApiTests : IClassFixture<ProviderAllowlistApiTests.Factory>
{
    private readonly HttpClient _client;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AiSuite:TranslationProvider", "Echo");
            builder.UseSetting("AiSuite:ChatDefaults:AllowedClientProviders:0", "Echo");
        }
    }

    public ProviderAllowlistApiTests(Factory f) => _client = f.CreateClient();

    [Fact]
    public async Task Chat_ProviderNotOnAllowlist_Returns400Validation()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "hi", provider = "KnowledgeBase" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Chat_ProviderOnAllowlist_CaseInsensitive_Succeeds()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "hi", provider = "echo" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
