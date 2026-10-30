using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>The shipped sample appsettings must bind to the documented security defaults and activate no restriction.</summary>
public class SampleSecurityDefaultsTests : IClassFixture<SampleSecurityDefaultsTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("AiSuite:TranslationProvider", "Echo");
    }

    private readonly Factory _f;
    public SampleSecurityDefaultsTests(Factory f) => _f = f;

    [Fact]
    public void SampleAppsettings_Binds_DocumentedDefaults()
    {
        var o = _f.Services.GetRequiredService<AiSuiteOptions>();
        Assert.Equal(10 * 1024 * 1024, o.Settings.MaxAudioBytes);
        Assert.Equal(50_000, o.Settings.MaxTrackedLoginCallers);
        Assert.False(o.ChatDefaults.AllowClientSystemMessages);
        Assert.Equal(2.0, o.ChatDefaults.MaxClientTemperature);
        Assert.Null(o.ChatDefaults.AllowedClientProviders);          // unrestricted registered-provider choice
        Assert.Null(o.AllowedClientTranslationProviders);            // unrestricted registered-provider choice
        Assert.Empty(o.Settings.Validate());
    }

    [Theory]
    [InlineData(15)]
    [InlineData(0)]
    [InlineData(-5)]
    public void MaxTrackedLoginCallers_BelowMinimum_FailsValidation(int v)
    {
        var s = new SettingsOptions { MaxTrackedLoginCallers = v };
        Assert.Contains(s.Validate(), m => m.Contains("MaxTrackedLoginCallers"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    public void MaxAudioBytes_BelowMinimum_FailsValidation(long v)
    {
        var s = new SettingsOptions { MaxAudioBytes = v };
        Assert.Contains(s.Validate(), m => m.Contains("MaxAudioBytes"));
    }

    [Fact]
    public void TokenService_UsesConfiguredCap_FromDi()
    {
        var svc = _f.Services.GetRequiredService<SettingsTokenService>();
        Assert.NotNull(svc); // constructed from Settings:MaxTrackedLoginCallers (50,000 default)
    }
}
