using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

public class TranslationProviderAllowlistApiTests : IClassFixture<TranslationProviderAllowlistApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("AiSuite:TranslationProvider", "Echo");
            b.UseSetting("AiSuite:RateLimit:Enabled", "false");
            b.UseSetting("AiSuite:AllowedClientTranslationProviders:0", "Echo");
        }
    }

    private readonly HttpClient _c;
    public TranslationProviderAllowlistApiTests(Factory f) => _c = f.CreateClient();

    public static IEnumerable<object[]> Routes() => new[]
    {
        new object[] { "/api/ai/translation/translate", (object)new { text = "hi", targetLanguage = "hi", provider = "LibreTranslate" } },
        new object[] { "/api/ai/translation/batch", (object)new { texts = new[] { "hi" }, targetLanguage = "hi", provider = "LibreTranslate" } },
        new object[] { "/api/ai/translation/detect-language", (object)new { text = "hi", provider = "LibreTranslate" } },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ProviderNotOnAllowlist_Is400Validation(string url, object body)
    {
        var res = await _c.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AllowedProvider_CaseInsensitive_Works()
    {
        var res = await _c.PostAsJsonAsync("/api/ai/translation/translate",
            new { text = "hi", targetLanguage = "hi", provider = "echo" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
