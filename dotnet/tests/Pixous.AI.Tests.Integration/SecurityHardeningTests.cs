using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// Production-hardening behavior over HTTP (offline providers, no real keys):
/// built-in API-key auth, per-IP rate limiting, exact-origin CORS, and the
/// fail-closed startup check when RequireAuthorization has no scheme.
/// Dummy key below is a generic test fixture - not a credential.
/// </summary>
public class SecurityHardeningTests
{
    private const string TestKey = "dummy-fixture-key-0123456789abcdef";
    private static readonly object ChatBody = new { message = "what are your services?" };

    private static void Offline(IWebHostBuilder b)
    {
        b.UseSetting("AiSuite:TranslationProvider", "Echo");
        b.UseSetting("AiSuite:ChatProvider", "KnowledgeBase");
    }

    // ── API-key auth ──────────────────────────────────────────────────────
    public sealed class AuthFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Offline(builder);
            builder.UseSetting("AiSuite:RateLimit:Enabled", "false");
            builder.UseSetting("AiSuite:Security:RequireAuthorization", "true");
            builder.UseSetting("AiSuite:Security:ApiKeys:0", TestKey);
        }
    }

    public sealed class Auth : IClassFixture<AuthFactory>
    {
        private readonly HttpClient _client;
        public Auth(AuthFactory f) => _client = f.CreateClient();

        [Fact]
        public async Task No_key_is_401_with_no_hint_body()
        {
            var res = await _client.PostAsJsonAsync("/api/ai/chat", ChatBody);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("X-API-Key", body);
            Assert.DoesNotContain(TestKey, body);
        }

        [Fact]
        public async Task Wrong_key_is_401_same_as_missing()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat");
            req.Headers.Add("X-API-Key", "dummy-fixture-key-0000000000000000");
            req.Content = JsonContent.Create(ChatBody);
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        [Fact]
        public async Task Correct_key_passes_and_key_never_echoes()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat");
            req.Headers.Add("X-API-Key", TestKey);
            req.Content = JsonContent.Create(ChatBody);
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":true", body);
            Assert.DoesNotContain(TestKey, body);
        }
    }

    // ── Fail-closed startup: auth required but no scheme anywhere ─────────
    public sealed class NoSchemeFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Offline(builder);
            builder.UseSetting("AiSuite:Security:RequireAuthorization", "true");
        }
    }

    public sealed class FailClosed
    {
        [Fact]
        public void RequireAuthorization_without_any_scheme_throws_at_startup()
        {
            using var f = new NoSchemeFactory();
            var ex = Assert.ThrowsAny<InvalidOperationException>(() => f.CreateClient());
            Assert.Contains("no authentication scheme", ex.Message + ex.InnerException?.Message);
        }
    }

    // ── Rate limiting ─────────────────────────────────────────────────────
    public sealed class RateLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Offline(builder);
            builder.UseSetting("AiSuite:RateLimit:Enabled", "true");
            builder.UseSetting("AiSuite:RateLimit:PermitLimit", "2");
            builder.UseSetting("AiSuite:RateLimit:WindowSeconds", "60");
        }
    }

    public sealed class RateLimit : IClassFixture<RateLimitFactory>
    {
        private readonly HttpClient _client;
        public RateLimit(RateLimitFactory f) => _client = f.CreateClient();

        [Fact]
        public async Task Third_request_in_window_is_429_with_retry_after()
        {
            var r1 = await _client.PostAsJsonAsync("/api/ai/chat", ChatBody);
            var r2 = await _client.PostAsJsonAsync("/api/ai/chat", ChatBody);
            var r3 = await _client.PostAsJsonAsync("/api/ai/chat", ChatBody);
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            Assert.Equal((HttpStatusCode)429, r3.StatusCode);
            Assert.True(r3.Headers.Contains("Retry-After"));
        }
    }

    // ── Exact-origin CORS ─────────────────────────────────────────────────
    public sealed class CorsFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Offline(builder);
            builder.UseSetting("AiSuite:Cors:Enabled", "true");
            builder.UseSetting("AiSuite:Cors:AllowedOrigins:0", "https://app.example.com");
        }
    }

    public sealed class Cors : IClassFixture<CorsFactory>
    {
        private readonly HttpClient _client;
        public Cors(CorsFactory f) => _client = f.CreateClient();

        private static HttpRequestMessage Preflight(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, "/api/ai/chat");
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            return req;
        }

        [Fact]
        public async Task Allowed_origin_gets_exact_allow_header()
        {
            var res = await _client.SendAsync(Preflight("https://app.example.com"));
            Assert.True(res.Headers.TryGetValues("Access-Control-Allow-Origin", out var v));
            Assert.Equal("https://app.example.com", Assert.Single(v));
        }

        [Fact]
        public async Task Other_origin_gets_no_cors_headers()
        {
            var res = await _client.SendAsync(Preflight("https://evil.example.net"));
            Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
        }

        [Fact]
        public async Task Cross_origin_post_carries_allow_header_for_allowed_origin()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat");
            req.Headers.Add("Origin", "https://app.example.com");
            req.Content = JsonContent.Create(ChatBody);
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            Assert.True(res.Headers.TryGetValues("Access-Control-Allow-Origin", out var v));
            Assert.Equal("https://app.example.com", Assert.Single(v));
        }
    }
}
