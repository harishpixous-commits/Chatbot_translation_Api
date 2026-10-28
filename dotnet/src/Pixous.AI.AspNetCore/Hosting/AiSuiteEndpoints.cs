using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers;

namespace Pixous.AI.AspNetCore.Hosting;

/// <summary>
/// Thin HTTP surface over <see cref="IChatService"/> / <see cref="ITranslationService"/>.
/// All responses use the standard <see cref="ApiResponse{T}"/> envelope
/// (except SSE streaming). All rules live in the services.
/// In Program.cs: <c>app.MapAiSuiteEndpoints();</c>
/// </summary>
public static class AiSuiteEndpoints
{
    /// <summary>Name of the module's exact-origin CORS policy (registered by AddAiSuite).</summary>
    public const string CorsPolicyName = "pixous-ai";

    public static IEndpointRouteBuilder MapAiSuiteEndpoints(
        this IEndpointRouteBuilder app,
        string prefix = "/api/ai")
    {
        var group = app.MapGroup(prefix);
        var opts = app.ServiceProvider.GetService(typeof(AiSuiteOptions)) as AiSuiteOptions;

        // Middleware is wired from here (in the recommended order CORS -> auth ->
        // rate limit) so a host that only calls AddAiSuite + MapAiSuiteEndpoints
        // is actually protected; hosts that wire these themselves should remove
        // their own calls (running a middleware twice counts/answers twice).
        if (opts?.Cors.Enabled == true)
        {
            group.RequireCors(CorsPolicyName);
            if (app is Microsoft.AspNetCore.Builder.IApplicationBuilder cab)
                cab.UseCors(CorsPolicyName);
        }
        if (opts?.Security.RequireAuthorization == true)
        {
            // Fail closed at startup: authorization metadata with no scheme
            // registered would otherwise surface as a per-request 500 at runtime.
            var schemes = app.ServiceProvider
                .GetService(typeof(IAuthenticationSchemeProvider)) as IAuthenticationSchemeProvider;
            var anyScheme = schemes is not null
                && schemes.GetAllSchemesAsync().GetAwaiter().GetResult().Any();
            if (!anyScheme)
                throw new InvalidOperationException(
                    "AiSuite:Security:RequireAuthorization is on but no authentication scheme is registered. " +
                    "Set AiSuite:Security:ApiKeys (built-in PixousApiKey scheme, X-API-Key header) " +
                    "or call AddAuthentication with the host's JWT/OIDC scheme.");
            group.RequireAuthorization();
            if (app is Microsoft.AspNetCore.Builder.IApplicationBuilder aab)
            {
                aab.UseAuthentication();
                aab.UseAuthorization();
            }
        }
        if (opts?.RateLimit.Enabled == true)
        {
            group.RequireRateLimiting("pixous-ai");
            // Safe default must actually enforce: wire the middleware from here so
            // a host that only calls AddAiSuite + MapAiSuiteEndpoints is still
            // cost-capped. Hosts that call app.UseRateLimiter() themselves should
            // remove that call (running it twice counts each request twice).
            if (app is Microsoft.AspNetCore.Builder.IApplicationBuilder ab)
                ab.UseRateLimiter();
        }

        group.MapPost("/chat", async (ChatRequestDto req, IChatService chat, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Ok(await chat.ChatAsync(req, ct).ConfigureAwait(false), http);
            }
            catch (Exception ex) { return Fail<ChatResponseDto>(http, ex, "AI_PROVIDER_UNAVAILABLE"); }
        })
        .WithName("AiSuite_Chat")
        .WithSummary("Non-streaming chat. Enable auth + rate limiting in production.");

        group.MapPost("/chat/stream", async (
            HttpContext http, ChatRequestDto req, IChatService chat, CancellationToken ct,
            Microsoft.Extensions.Logging.ILoggerFactory? logs = null) =>

        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var log = logs?.CreateLogger("Pixous.AI.ChatStream");
            try
            {
                await foreach (var chunk in chat.StreamChatAsync(req, ct).ConfigureAwait(false))
                {
                    await http.Response.WriteAsync($"data: {chunk.Delta.Replace("\n", "\\n")}\n\n", ct).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    if (chunk.IsFinal) break;
                }
                await http.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any failure (validation, provider rejection, upstream stream dying
                // mid-SSE) becomes an explicit SSE error frame the client can show -
                // never a silent blank stream. Client cancellations are not errors.
                var (status, code) = ex switch
                {
                    ArgumentException => (400, "VALIDATION"),
                    System.IO.InvalidDataException => (502, "UPSTREAM_ABORTED"),
                    System.Net.Http.HttpRequestException => (502, "UPSTREAM_UNREACHABLE"),
                    _ => (500, "SERVER_ERROR"),
                };
                // Client-facing frame: FIXED allowlisted text per stable code.
                // Vendor/provider text (even sanitized) is not a disclosure
                // boundary - it can echo arbitrary private prompt text, so no
                // provider-authored string reaches the client. Bounded
                // sanitized diagnostics go to server-side logs only.
                var clientText = code switch
                {
                    "VALIDATION" => Pixous.AI.Core.Services.ProviderErrorSanitizer.SanitizeForClient(ex.Message),
                    "UPSTREAM_ABORTED" => "The response stream was interrupted. Please try again.",
                    "UPSTREAM_UNREACHABLE" => "The AI service is temporarily unavailable. Please try again shortly.",
                    _ => "An unexpected error occurred. Please try again.",
                };
                // Server logs are a persistence surface too: category +
                // exception type + correlation id only, never ex.Message
                // (provider text can echo arbitrary private request content).
                log?.LogWarning("Chat stream failed. Code={Code} Type={Type} Trace={TraceId}",
                    code, ex.GetType().Name, TraceId(http));
                if (!http.Response.HasStarted)
                    http.Response.StatusCode = status;
                var payload = System.Text.Json.JsonSerializer.Serialize(
                    new { error = clientText, code });
                await http.Response.WriteAsync($"data: {payload}\n\n", CancellationToken.None).ConfigureAwait(false);
            }
        })
        .WithName("AiSuite_ChatStream")
        .WithSummary("Streaming chat over Server-Sent Events.");

        group.MapPost("/translation/translate", async (TranslateRequestDto req, ITranslationService t, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Ok(await t.TranslateAsync(req, ct).ConfigureAwait(false), http);
            }
            catch (Exception ex) { return Fail<TranslateResponseDto>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
        })
        .WithName("AiSuite_Translate")
        .WithSummary("Translate text to a configured language.");

        group.MapPost("/translation/batch", async (TranslateBatchRequestDto req, ITranslationService t, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Ok(await t.TranslateBatchAsync(req, ct).ConfigureAwait(false), http);
            }
            catch (Exception ex) { return Fail<TranslateBatchResponseDto>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
        })
        .WithName("AiSuite_TranslateBatch")
        .WithSummary("Translate 1-100 texts in one call.");

        group.MapPost("/translation/detect-language", async (DetectRequestDto req, ITranslationService t, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Ok(await t.DetectAsync(req, ct).ConfigureAwait(false), http);
            }
            catch (Exception ex) { return Fail<DetectResponseDto>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
        })
        .WithName("AiSuite_DetectLanguage")
        .WithSummary("Detect the language of a text.");

        group.MapGet("/translation/languages", async (ITranslationService t, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Ok(await t.GetLanguagesAsync(ct).ConfigureAwait(false), http);
            }
            catch (Exception ex) { return Fail<IReadOnlyList<SupportedLanguage>>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
        })
        .WithName("AiSuite_Languages")
        .WithSummary("Languages supported by the active translation provider.");

        group.MapGet("/providers", (IChatProviderFactory c, ITranslationProviderFactory tr, HttpContext http) =>
            Ok(new
            {
                chatProviders = c.RegisteredProviders,
                translationProviders = tr.RegisteredProviders
            }, http))
        .WithName("AiSuite_Providers")
        .WithSummary("Registered provider names (safe to expose).");

        group.MapGet("/info", (AiSuiteOptions o, IConfiguration cfg, AiSuiteRuntimeSelection sel,
            SettingsStore store, IVoiceService voice, HttpContext http) =>
        {
            static bool HasSecret(IConfiguration cfg, string? configKey) =>
                !string.IsNullOrWhiteSpace(configKey) && !string.IsNullOrWhiteSpace(cfg[configKey]);

            var chatNeedsKey = (sel.ChatProvider ?? o.ChatProvider).Equals("OpenAiCompatible", StringComparison.OrdinalIgnoreCase)
                && o.OpenAiCompatible.RequireApiKey;
            var activeTranslator = sel.TranslationProvider ?? o.TranslationProvider;
            var translatorNeedsKey = activeTranslator.Equals("AzureTranslator", StringComparison.OrdinalIgnoreCase)
                || (activeTranslator.Equals("LibreTranslate", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(o.LibreTranslate.ApiKeyConfigKey));

            var voiceCfg = store.GetVoice();
            return Ok(new
            {
                chatProvider = o.ChatProvider,
                translationProvider = o.TranslationProvider,
                effectiveChatProvider = sel.ChatProvider ?? o.ChatProvider,
                effectiveTranslationProvider = activeTranslator,
                defaultLanguage = o.Languages.Default,
                supportedLanguages = o.Languages.Supported,
                // Settings panel availability + voice mode for the client widget.
                // Flags/providers only — never endpoints, keys or other secrets.
                settings = new { enabled = o.Settings.Enabled },
                llmKeysConfigured = store.GetKeys().Count,
                knowledgeDocuments = store.GetDocuments().Count,
                voice = new
                {
                    sttEnabled = voiceCfg.SttEnabled,
                    sttProvider = voiceCfg.SttProvider,
                    ttsEnabled = voiceCfg.TtsEnabled,
                    ttsProvider = voiceCfg.TtsProvider
                },
                // Pointers + configured flags only — never secret values.
                services = new object[]
                {
                    new
                    {
                        name = "Chat",
                        provider = sel.ChatProvider ?? o.ChatProvider,
                        apiKeyConfigKey = chatNeedsKey ? o.OpenAiCompatible.ApiKeyConfigKey : null,
                        apiKeyConfigured = !chatNeedsKey || HasSecret(cfg, o.OpenAiCompatible.ApiKeyConfigKey)
                    },
                    new
                    {
                        name = "Translation",
                        provider = activeTranslator,
                        apiKeyConfigKey = activeTranslator.Equals("AzureTranslator", StringComparison.OrdinalIgnoreCase)
                            ? o.AzureTranslator.ApiKeyConfigKey
                            : o.LibreTranslate.ApiKeyConfigKey,
                        apiKeyConfigured = !translatorNeedsKey || HasSecret(cfg,
                            activeTranslator.Equals("AzureTranslator", StringComparison.OrdinalIgnoreCase)
                                ? o.AzureTranslator.ApiKeyConfigKey
                                : o.LibreTranslate.ApiKeyConfigKey)
                    }
                }
            }, http);
        })
        .WithName("AiSuite_Info")
        .WithSummary("Providers, key status, languages, settings/voice flags. Never exposes keys.");

        group.MapGet("/selection", (
            AiSuiteOptions o, AiSuiteRuntimeSelection sel,
            IChatProviderFactory c, ITranslationProviderFactory tr, HttpContext http) =>
            Ok(new
            {
                chatProvider = sel.ChatProvider ?? o.ChatProvider,
                translationProvider = sel.TranslationProvider ?? o.TranslationProvider,
                runtimeOverride = new { chatProvider = sel.ChatProvider, translationProvider = sel.TranslationProvider },
                availableChatProviders = c.RegisteredProviders,
                availableTranslationProviders = tr.RegisteredProviders
            }, http))
        .WithName("AiSuite_GetSelection")
        .WithSummary("Current effective selection. Protect with admin auth in production.");

        group.MapPost("/selection", (
            SelectionRequestDto req, AiSuiteRuntimeSelection sel,
            IChatProviderFactory c, ITranslationProviderFactory tr, HttpContext http,
            SettingsTokenService tokens) =>
        {
            // Admin-only: a valid ⚙ settings session token is required whenever
            // the settings surface is enabled; otherwise the host's auth policy
            // (Security:RequireAuthorization) is the only gate — see security.md.
            if (opts?.Settings.Enabled == true &&
                !tokens.Validate(AiSuiteSettingsEndpoints.Token(http)))
                return AiSuiteSettingsEndpoints.Unauthorized(http,
                    "Settings session required to switch providers. Enter the settings password first.");

            var error = sel.TrySet(req.ChatProvider, req.TranslationProvider,
                c.RegisteredProviders, tr.RegisteredProviders);
            return error is null
                ? Ok(new
                {
                    chatProvider = sel.ChatProvider,
                    translationProvider = sel.TranslationProvider,
                    message = "Selection updated. Null means 'use configured default'."
                }, http)
                : Fail<object>(http, new ArgumentException(error), "UNKNOWN_PROVIDER");
        })
        .WithName("AiSuite_SetSelection")
        .WithSummary("Switch providers live without restart. Protect with admin auth in production.");

        // Settings panel surface (password-gated) + public voice runtime routes.
        app.MapAiSuiteSettingsEndpoints(prefix);

        // Shared service surface for consumer projects (X-Api-Key project keys).
        app.MapAiSuiteServiceApi();

        return app;
    }

    // ── envelope helpers (shared with AiSuiteSettingsEndpoints) ──
    internal static string TraceId(HttpContext http) =>
        Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;

    internal static IResult Ok<T>(T data, HttpContext http) =>
        Results.Ok(new ApiResponse<T>(true, data, null, TraceId(http)));

    internal static IResult Fail<T>(HttpContext http, Exception ex, string upstreamCode)
    {
        var trace = TraceId(http);
        return ex switch
        {
            ArgumentException aex => Results.BadRequest(
                new ApiResponse<T>(false, default, new ApiError("VALIDATION_ERROR", aex.Message), trace)),
            InvalidOperationException ioex => Results.Problem(
                title: ioex.Message, statusCode: 503,
                extensions: new Dictionary<string, object?> { ["success"] = false, ["code"] = "PROVIDER_NOT_CONFIGURED", ["traceId"] = trace }),
            HttpRequestException => Results.Problem(
                title: "The AI service is temporarily unavailable.",
                statusCode: 502,
                extensions: new Dictionary<string, object?> { ["success"] = false, ["code"] = upstreamCode, ["traceId"] = trace }),
            _ => Results.Problem(
                title: "An unexpected error occurred.",
                statusCode: 500,
                extensions: new Dictionary<string, object?> { ["success"] = false, ["code"] = "INTERNAL_ERROR", ["traceId"] = trace }),
        };
    }
}
