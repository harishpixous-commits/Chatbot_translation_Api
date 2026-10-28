using Pixous.AI.AspNetCore.Extensions;
using Pixous.AI.AspNetCore.Hosting;

// ── Sample host: the 3-line enterprise integration ───────────────────────
// 1. builder.Services.AddAiSuite(builder.Configuration);
// 2. app.MapAiSuiteEndpoints();            // /api/ai/*
// 3. Configure AiSuite section + secrets (see appsettings.json + docs).
//
// Production hardening (see docs/security.md) - all wired by MapAiSuiteEndpoints
// from configuration, no extra middleware calls needed here:
// - auth: set AiSuite:Security:RequireAuthorization=true plus either
//   AiSuite:Security:ApiKeys (built-in X-API-Key scheme) or your own JWT/OIDC
//   AddAuthentication; startup fails closed if no scheme is available,
// - rate limit: AiSuite:RateLimit:Enabled (ON by default since 1.0.1),
// - CORS: AiSuite:Cors:Enabled + AiSuite:Cors:AllowedOrigins (exact origins only).

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAiSuite(builder.Configuration);

var app = builder.Build();

app.UseStaticFiles(); // serves wwwroot/demo.html (sample-only demo page)
app.MapAiSuiteEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "Pixous AI Sample", info = "/api/ai/info" }));

app.Run();

// Exposed for integration tests (WebApplicationFactory{Program}).
public partial class Program { }
