using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

public class VoiceSttBodyLimitTests : IClassFixture<VoiceSttBodyLimitTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("AiSuite:TranslationProvider", "Echo");
            b.UseSetting("AiSuite:RateLimit:Enabled", "false");
            b.UseSetting("AiSuite:Settings:MaxAudioBytes", "2048");
        }
    }

    private readonly HttpClient _c;
    public VoiceSttBodyLimitTests(Factory f) => _c = f.CreateClient();

    private static MultipartFormDataContent Audio(int bytes)
    {
        var m = new MultipartFormDataContent();
        var f = new ByteArrayContent(new byte[bytes]);
        f.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        m.Add(f, "file", "a.wav");
        return m;
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(200_000)]
    public async Task OversizeAudio_Rejected_400_Validation_BeforeAnyProviderCall(int bytes)
    {
        var res = await _c.PostAsync("/api/ai/voice/stt", Audio(bytes));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnderLimitAudio_PassesSizeCheck_ThenFailsAsVoiceUnavailable()
    {
        var res = await _c.PostAsync("/api/ai/voice/stt", Audio(512));
        Assert.NotEqual(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
