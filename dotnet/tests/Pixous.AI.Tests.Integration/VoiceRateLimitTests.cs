using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>Public voice routes spend provider quota, so they share the per-IP rate limit with /chat.</summary>
public class VoiceRateLimitTests : IClassFixture<VoiceRateLimitTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("AiSuite:TranslationProvider", "Echo");
            b.UseSetting("AiSuite:RateLimit:Enabled", "true");
            b.UseSetting("AiSuite:RateLimit:PermitLimit", "2");
            b.UseSetting("AiSuite:RateLimit:WindowSeconds", "60");
        }
    }

    private readonly HttpClient _c;
    public VoiceRateLimitTests(Factory f) => _c = f.CreateClient();

    [Fact]
    public async Task VoiceTts_IsRateLimited_Returns429WithRetryAfter()
    {
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++) last = await _c.PostAsJsonAsync("/api/ai/voice/tts", new { text = "hi" });
        Assert.Equal((HttpStatusCode)429, last!.StatusCode);
        Assert.True(last.Headers.Contains("Retry-After"));
    }
}
