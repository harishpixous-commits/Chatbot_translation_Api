using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Pixous.AI.Core.Services;

namespace Pixous.AI.AspNetCore.Hosting;

public sealed record CreateServiceKeyRequestDto(string Name);

/// <summary>
/// Shared, tech-neutral HTTP surface for consumer projects ("plug-and-play"):
/// any backend in any language calls <c>{host}/api/ai/v1/*</c> with an
/// <c>X-Api-Key</c> project key issued from the settings panel. Keys are
/// per-project: each gets its own rate-limit window, usage counters and
/// session namespace (one project cannot read another project's sessions).
/// Server side only — the key must NEVER ship to a browser or mobile app.
/// Provider credentials stay on this server; consumers only ever see their
/// project key. Map with <c>app.MapAiSuiteServiceApi();</c> (mapped
/// automatically by <see cref="AiSuiteEndpoints.MapAiSuiteEndpoints"/>).
/// </summary>
public static class AiSuiteServiceApiEndpoints
{
    public const string ApiKeyHeader = "X-Api-Key";

    public static IEndpointRouteBuilder MapAiSuiteServiceApi(
        this IEndpointRouteBuilder app,
        string prefix = "/api/ai/v1")
    {
        var group = app.MapGroup(prefix);

        group.MapPost("/chat", async (ChatRequestDto req, HttpContext http, IChatService chat,
                ServiceKeyStore keys, CancellationToken ct) =>
            {
                var gate = Gate(http, keys);
                if (gate.Key is not { } key) return KeyRejected(http, gate);
                try
                {
                    var scoped = req with { SessionId = ScopeSession(key.Id, req.SessionId) };
                    var result = await chat.ChatAsync(scoped, ct).ConfigureAwait(false);
                    keys.RecordUsage(key.Id);
                    return AiSuiteEndpoints.Ok(result, http);
                }
                catch (Exception ex) { return AiSuiteEndpoints.Fail<ChatResponseDto>(http, ex, "AI_PROVIDER_UNAVAILABLE"); }
            })
            .WithName("AiSuite_ServiceApi_Chat")
            .WithSummary("Shared-service chat with a project API key (X-Api-Key header).");

        group.MapPost("/chat/stream", async (HttpContext http, ChatRequestDto req, IChatService chat,
                ServiceKeyStore keys, CancellationToken ct,
                Microsoft.Extensions.Logging.ILoggerFactory? logs = null) =>
            {
                var gate = Gate(http, keys);
                if (gate.Key is not { } key)
                {
                    http.Response.StatusCode = gate.Code == "SERVICE_KEY_RATE_LIMITED"
                        ? StatusCodes.Status429TooManyRequests
                        : StatusCodes.Status401Unauthorized;
                    await http.Response.WriteAsJsonAsync(
                        new ApiResponse<object>(false, null,
                            new ApiError(gate.Code!, gate.Message!),
                            AiSuiteEndpoints.TraceId(http)), ct).ConfigureAwait(false);
                    return;
                }
                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                var scoped = req with { SessionId = ScopeSession(key.Id, req.SessionId) };
                try
                {
                    await foreach (var chunk in chat.StreamChatAsync(scoped, ct).ConfigureAwait(false))
                    {
                        await http.Response.WriteAsync($"data: {chunk.Delta.Replace("\n", "\\n")}\n\n", ct).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                        if (chunk.IsFinal) break;
                    }
                    await http.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
                    keys.RecordUsage(key.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!http.Response.HasStarted)
                        http.Response.StatusCode = ex switch
                        {
                            ArgumentException => 400,
                            System.IO.InvalidDataException => 502,
                            System.Net.Http.HttpRequestException => 502,
                            _ => 500,
                        };
                    var code = ex switch
                    {
                        ArgumentException => "VALIDATION",
                        System.IO.InvalidDataException => "UPSTREAM_ABORTED",
                        System.Net.Http.HttpRequestException => "UPSTREAM_UNREACHABLE",
                        _ => "SERVER_ERROR",
                    };
                    // Client-facing frame: FIXED allowlisted text per stable
                    // code; provider-authored strings never reach the client.
                    // Bounded sanitized diagnostics go to server logs only.
                    var clientText = code switch
                    {
                        "VALIDATION" => Pixous.AI.Core.Services.ProviderErrorSanitizer.SanitizeForClient(ex.Message),
                        "UPSTREAM_ABORTED" => "The response stream was interrupted. Please try again.",
                        "UPSTREAM_UNREACHABLE" => "The AI service is temporarily unavailable. Please try again shortly.",
                        _ => "An unexpected error occurred. Please try again.",
                    };
                    logs?.CreateLogger("Pixous.AI.ServiceApiChatStream").LogWarning(
                        "Service API chat stream failed. Code={Code} Type={Type} Trace={TraceId}",
                        code, ex.GetType().Name, http.TraceIdentifier);
                    var payload = System.Text.Json.JsonSerializer.Serialize(new { error = clientText, code });
                    await http.Response.WriteAsync($"data: {payload}\n\n", CancellationToken.None).ConfigureAwait(false);
                }
            })
            .WithName("AiSuite_ServiceApi_ChatStream")
            .WithSummary("Shared-service streaming chat (SSE) with a project API key.");

        group.MapPost("/translate", async (TranslateRequestDto req, HttpContext http, ITranslationService t,
                ServiceKeyStore keys, CancellationToken ct) =>
            {
                var gate = Gate(http, keys);
                if (gate.Key is not { } key) return KeyRejected(http, gate);
                try
                {
                    var result = await t.TranslateAsync(req, ct).ConfigureAwait(false);
                    keys.RecordUsage(key.Id);
                    return AiSuiteEndpoints.Ok(result, http);
                }
                catch (Exception ex) { return AiSuiteEndpoints.Fail<TranslateResponseDto>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
            })
            .WithName("AiSuite_ServiceApi_Translate")
            .WithSummary("Shared-service translation with a project API key.");

        group.MapPost("/translate/batch", async (TranslateBatchRequestDto req, HttpContext http, ITranslationService t,
                ServiceKeyStore keys, CancellationToken ct) =>
            {
                var gate = Gate(http, keys);
                if (gate.Key is not { } key) return KeyRejected(http, gate);
                try
                {
                    var result = await t.TranslateBatchAsync(req, ct).ConfigureAwait(false);
                    keys.RecordUsage(key.Id);
                    return AiSuiteEndpoints.Ok(result, http);
                }
                catch (Exception ex) { return AiSuiteEndpoints.Fail<TranslateBatchResponseDto>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
            })
            .WithName("AiSuite_ServiceApi_TranslateBatch")
            .WithSummary("Shared-service batch translation with a project API key.");

        group.MapPost("/detect-language", async (DetectRequestDto req, HttpContext http, ITranslationService t,
                ServiceKeyStore keys, CancellationToken ct) =>
            {
                var gate = Gate(http, keys);
                if (gate.Key is not { } key) return KeyRejected(http, gate);
                try
                {
                    var result = await t.DetectAsync(req, ct).ConfigureAwait(false);
                    keys.RecordUsage(key.Id);
                    return AiSuiteEndpoints.Ok(result, http);
                }
                catch (Exception ex) { return AiSuiteEndpoints.Fail<DetectResponseDto>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
            })
            .WithName("AiSuite_ServiceApi_DetectLanguage")
            .WithSummary("Shared-service language detection with a project API key.");

        group.MapGet("/languages", async (HttpContext http, ITranslationService t,
                ServiceKeyStore keys, CancellationToken ct) =>
            {
                var gate = Gate(http, keys);
                if (gate.Key is not { }) return KeyRejected(http, gate);
                try
                {
                    return AiSuiteEndpoints.Ok(await t.GetLanguagesAsync(ct).ConfigureAwait(false), http);
                }
                catch (Exception ex) { return AiSuiteEndpoints.Fail<IReadOnlyList<Pixous.AI.Abstractions.SupportedLanguage>>(http, ex, "TRANSLATION_PROVIDER_UNAVAILABLE"); }
            })
            .WithName("AiSuite_ServiceApi_Languages")
            .WithSummary("List supported languages (project API key required).");

        return app;
    }

    /// <summary>Outcome of the X-Api-Key + per-key rate-limit gate.</summary>
    private sealed record GateResult(ServiceKeyInfo? Key, string? Code, string? Message)
    {
        public static readonly GateResult Invalid = new(null, "SERVICE_KEY_INVALID",
            "Missing, unknown or revoked project API key. Send the X-Api-Key header issued by the settings panel.");
        public static readonly GateResult RateLimited = new(null, "SERVICE_KEY_RATE_LIMITED",
            $"This project key exceeded {ServiceKeyStore.DefaultPerMinuteLimit} requests/minute. Wait for the window to reset.");
        public static GateResult Allowed(ServiceKeyInfo key) => new(key, null, null);
    }

    /// <summary>Validates X-Api-Key and the per-key rate window.</summary>
    private static GateResult Gate(HttpContext http, ServiceKeyStore keys)
    {
        var raw = http.Request.Headers[ApiKeyHeader].FirstOrDefault();
        var key = keys.Validate(raw);
        if (key is null) return GateResult.Invalid;
        if (!keys.TryAcquirePermit(key.Id))
        {
            http.Response.Headers.RetryAfter = "60";
            return GateResult.RateLimited;
        }
        return GateResult.Allowed(key);
    }

    private static IResult KeyRejected(HttpContext http, GateResult gate) =>
        Results.Json(
            new ApiResponse<object>(false, null,
                new ApiError(gate.Code!, gate.Message!),
                AiSuiteEndpoints.TraceId(http)),
            statusCode: gate.Code == "SERVICE_KEY_RATE_LIMITED"
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status401Unauthorized);

    /// <summary>Namespaces the session id per project key so projects cannot see each other's conversations.</summary>
    private static string? ScopeSession(string keyId, string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? sessionId : $"{keyId}:{sessionId}";
}
