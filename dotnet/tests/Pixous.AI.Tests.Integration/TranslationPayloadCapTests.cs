using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

public class TranslationPayloadCapTests : IClassFixture<TranslationPayloadCapTests.Factory>
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
    public TranslationPayloadCapTests(Factory f) => _c = f.CreateClient();

    private static async Task AssertValidation400(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Batch_OversizedItem_Is400_LikeSingleRoute()
    {
        var res = await _c.PostAsJsonAsync("/api/ai/translation/batch",
            new { texts = new[] { "ok", new string('a', 4001) }, targetLanguage = "hi" });
        await AssertValidation400(res);
    }

    [Fact]
    public async Task Batch_ItemsAtLimit_StillWork()
    {
        var res = await _c.PostAsJsonAsync("/api/ai/translation/batch",
            new { texts = new[] { new string('a', 2000), new string('b', 4000) }, targetLanguage = "hi" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Detect_OversizedText_Is400()
    {
        var res = await _c.PostAsJsonAsync("/api/ai/translation/detect-language", new { text = new string('a', 4001) });
        await AssertValidation400(res);
    }
}
