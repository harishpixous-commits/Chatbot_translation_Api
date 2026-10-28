using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// Secure first setup: outside Development the shipped default settings
/// password must be refused until the operator configures a real one.
/// </summary>
public class ProductionStartupTests : IClassFixture<ProductionStartupTests.Factory>
{
    private readonly HttpClient _client;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string StoreDir { get; } =
            Path.Combine(Path.GetTempPath(), "pixous-settings-prod-" + Guid.NewGuid().ToString("n"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("AiSuite:Settings:StoreDirectory", StoreDir);
            // appsettings.json ships the default "pixous" — do NOT override it here.
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (Directory.Exists(StoreDir)) Directory.Delete(StoreDir, true); }
            catch { /* best effort */ }
        }
    }

    public ProductionStartupTests(Factory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ShippedDefaultPassword_RefusedInProduction()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/settings/login", new { password = "pixous" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("shipped default", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AiSuite__Settings__Password", body);
    }

    [Fact]
    public async Task PublicChat_StillWorksInProduction()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat", new { message = "hi" });
        res.EnsureSuccessStatusCode();
    }
}
