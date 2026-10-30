using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>An unknown caller-chosen provider is a controlled 400 that does not enumerate registered providers.</summary>
public class UnknownProviderApiTests : IClassFixture<UnknownProviderApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("AiSuite:TranslationProvider", "Echo");
            b.UseSetting("AiSuite:RateLimit:Enabled", "false");
        }
    }

    private readonly HttpClient _c;
    public UnknownProviderApiTests(Factory f) => _c = f.CreateClient();

    public static IEnumerable<object[]> Routes() => new[]
    {
        new object[] { "/api/ai/chat", (object)new { message = "hi", provider = "Nope" } },
        new object[] { "/api/ai/translation/translate", (object)new { text = "hi", targetLanguage = "hi", provider = "Nope" } },
        new object[] { "/api/ai/translation/batch", (object)new { texts = new[] { "hi" }, targetLanguage = "hi", provider = "Nope" } },
        new object[] { "/api/ai/translation/detect-language", (object)new { text = "hi", provider = "Nope" } },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task UnknownProvider_Is400_AndDoesNotListRegisteredProviders(string url, object body)
    {
        var res = await _c.PostAsJsonAsync(url, body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("Registered", text);
        Assert.DoesNotContain("KnowledgeBase", text);
        Assert.DoesNotContain("AiSuite:", text);
    }
}
