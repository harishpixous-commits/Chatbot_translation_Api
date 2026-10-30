using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Documents;
using Pixous.AI.Core.Services;
using Pixous.AI.Providers;

namespace Pixous.AI.AspNetCore.Hosting;

// ── Settings request DTOs ────────────────────────────────────────────────
public sealed record LoginRequestDto(string Password);
public sealed record ChangePasswordRequestDto(string CurrentPassword, string NewPassword);

public sealed record AddLlmKeyRequestDto(
    string? Name, string Vendor, string? Endpoint, string? Model, string? ApiKey,
    int? MaxTokens, double? Temperature, bool? Enabled);

public sealed record UpdateLlmKeyRequestDto(
    string? Name, string? Vendor, string? Endpoint, string? Model, string? ApiKey,
    int? MaxTokens, double? Temperature, bool? Enabled, string? Move);

public sealed record TtsRequestDto(string Text, string? Voice, string? Language);

/// <summary>
/// Password-gated admin surface (⚙ panel): multi-vendor LLM API keys with
/// auto-fallback, voice (STT/TTS) configuration, knowledge-base uploads and
/// the settings password. Mounted under <c>{prefix}/settings</c> (+ public
/// runtime voice routes under <c>{prefix}/voice</c>) — mapped automatically by
/// <see cref="AiSuiteEndpoints.MapAiSuiteEndpoints"/>.
/// Secrets contract: API keys go IN here (authenticated) and are only ever
/// returned masked. Nothing in this file logs or echoes a key.
/// </summary>
public static class AiSuiteSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiSuiteSettingsEndpoints(
        this IEndpointRouteBuilder app,
        string prefix = "/api/ai")
    {
        var opts = app.ServiceProvider.GetService(typeof(AiSuiteOptions)) as AiSuiteOptions;
        if (opts is null || !opts.Settings.Enabled) return app;

        var settings = app.MapGroup(prefix + "/settings");
        var voice = app.MapGroup(prefix + "/voice");
        // Public voice routes spend STT/TTS provider quota: apply the same per-IP cap as /chat.
        if (opts.RateLimit.Enabled) voice.RequireRateLimiting("pixous-ai");

        // ── auth ─────────────────────────────────────────────────────────
        settings.MapPost("/login", (LoginRequestDto req, SettingsTokenService tokens,
            SettingsStore store, HttpContext http) =>
        {
            var caller = Caller(http);
            var lockout = tokens.RemainingLockout(caller);
            if (lockout > TimeSpan.Zero)
                return Unauthorized(http,
                    $"Too many failed attempts. Try again in {Math.Ceiling(lockout.TotalSeconds)}s.");

            // Not counted as a failed attempt: this is a deployment blocker, not a guess.
            if (store.IsShippedDefaultPasswordBlocked())
                return Unauthorized(http,
                    "The shipped default settings password is refused outside Development. " +
                    "Set a real password first: AiSuite__Settings__Password via environment " +
                    "variable, User Secrets or Key Vault, then restart and log in.");

            if (store.VerifyPassword(req.Password ?? string.Empty))
            {
                tokens.RegisterSuccess(caller);
                var (token, expires) = tokens.Issue(opts.Settings);
                return AiSuiteEndpoints.Ok(new { token, expiresAtUtc = expires }, http);
            }

            var remaining = tokens.RegisterFailure(caller);
            return Unauthorized(http, remaining > TimeSpan.Zero
                ? $"Incorrect password. Locked for {Math.Ceiling(remaining.TotalSeconds)}s."
                : "Incorrect password.");
        })
        .WithName("AiSuite_SettingsLogin")
        .WithSummary("Exchange the settings password for a session token.");

        settings.MapPost("/logout", (HttpContext http, SettingsTokenService tokens) =>
        {
            tokens.Revoke(Token(http));
            return AiSuiteEndpoints.Ok(true, http);
        }).WithName("AiSuite_SettingsLogout");

        // ── snapshot ─────────────────────────────────────────────────────
        settings.MapGet("", (HttpContext http, SettingsTokenService tokens, SettingsStore store,
            AiSuiteOptions o, AiSuiteRuntimeSelection selection) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;

            var keys = store.GetKeys();
            var voice = store.GetVoice();
            return AiSuiteEndpoints.Ok(new
            {
                security = new
                {
                    passwordSource = store.PasswordSource,
                    persistKeys = o.Settings.AllowPersistKeys,
                    storeDirectory = store.DirectoryName,
                    minPasswordLength = o.Settings.MinPasswordLength,
                    tokenHours = o.Settings.TokenHours
                },
                chatProvider = selection.ChatProvider ?? o.ChatProvider,
                llmKeys = keys.Select(k =>
                {
                    var (state, until, lastStatus, lastError) = store.GetKeyStatus(k.Id);
                    return (object)new
                    {
                        k.Id, k.Name, k.Vendor, k.Endpoint, k.Model,
                        apiKeyMasked = SettingsStore.MaskKey(k.ApiKey),
                        hasKey = !string.IsNullOrEmpty(k.ApiKey),
                        k.MaxTokens, k.Temperature, k.Enabled, k.Priority,
                        status = new { state, untilUtc = until, lastStatus, lastError }
                    };
                }).ToList(),
                voice = new
                {
                    voice.SttEnabled, voice.SttProvider, voice.SttEndpoint, voice.SttModel,
                    sttKeyMasked = SettingsStore.MaskKey(voice.SttApiKey),
                    sttHasKey = !string.IsNullOrEmpty(voice.SttApiKey),
                    voice.TtsEnabled, voice.TtsProvider, voice.TtsEndpoint, voice.TtsModel,
                    voice.TtsVoice,
                    ttsKeyMasked = SettingsStore.MaskKey(voice.TtsApiKey),
                    ttsHasKey = !string.IsNullOrEmpty(voice.TtsApiKey)
                },
                documents = store.GetDocuments(),
                vendorPresets = LlmVendors.All
            }, http);
        })
        .WithName("AiSuite_SettingsGet")
        .WithSummary("Full settings snapshot — keys masked, never raw.");

        // ── password ─────────────────────────────────────────────────────
        settings.MapPut("/password", (ChangePasswordRequestDto req, HttpContext http,
            SettingsTokenService tokens, SettingsStore store, AiSuiteOptions o) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            if (!store.VerifyPassword(req.CurrentPassword ?? string.Empty))
                return Unauthorized(http, "Current password is incorrect.");
            var next = req.NewPassword ?? string.Empty;
            if (next.Length < o.Settings.MinPasswordLength)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"New password must be at least {o.Settings.MinPasswordLength} characters."),
                    "VALIDATION_ERROR");
            store.SetPassword(next);
            return AiSuiteEndpoints.Ok(new { changed = true, passwordSource = store.PasswordSource }, http);
        })
        .WithName("AiSuite_SettingsChangePassword");

        // ── LLM API keys ─────────────────────────────────────────────────
        settings.MapPost("/llm-keys", (AddLlmKeyRequestDto req, HttpContext http,
            SettingsTokenService tokens, SettingsStore store) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            try
            {
                var entry = BuildEntry(req.Name, req.Vendor, req.Endpoint, req.Model, req.ApiKey,
                    req.MaxTokens, req.Temperature, req.Enabled ?? true);
                var added = store.AddKey(entry);
                return AiSuiteEndpoints.Ok(Masked(added, store), http);
            }
            catch (ArgumentException ex)
            {
                return AiSuiteEndpoints.Fail<object>(http, ex, "VALIDATION_ERROR");
            }
        })
        .WithName("AiSuite_AddLlmKey")
        .WithSummary("Save an LLM API key (any vendor). Response carries a masked key only.");

        settings.MapPut("/llm-keys/{id}", (string id, UpdateLlmKeyRequestDto req, HttpContext http,
            SettingsTokenService tokens, SettingsStore store) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            var existing = store.GetKey(id);
            if (existing is null) return NotFound(http, "Key not found.");

            // Build the proposed final state first so validation failures never
            // half-mutate the stored entry.
            var proposed = existing.Clone();
            if (!string.IsNullOrWhiteSpace(req.Name)) proposed.Name = req.Name.Trim();
            if (req.Vendor is not null) proposed.Vendor = req.Vendor.Trim();
            if (req.Endpoint is not null) proposed.Endpoint = req.Endpoint.Trim();
            if (req.Model is not null) proposed.Model = req.Model.Trim();
            if (req.ApiKey is not null) proposed.ApiKey = req.ApiKey.Trim();
            if (req.MaxTokens is not null) proposed.MaxTokens = req.MaxTokens;
            if (req.Temperature is not null) proposed.Temperature = req.Temperature;
            if (req.Enabled is not null) proposed.Enabled = req.Enabled.Value;

            try
            {
                var validated = BuildEntry(proposed.Name, proposed.Vendor, proposed.Endpoint,
                    proposed.Model, proposed.ApiKey, proposed.MaxTokens, proposed.Temperature, proposed.Enabled);
                // Keep the identity + preserved extras.
                validated.Id = existing.Id;
                validated.Headers = existing.Headers;
                store.UpdateKey(id, k =>
                {
                    k.Name = validated.Name;
                    k.Vendor = validated.Vendor;
                    k.Endpoint = validated.Endpoint;
                    k.Model = validated.Model;
                    k.ApiKey = validated.ApiKey;
                    k.MaxTokens = validated.MaxTokens;
                    k.Temperature = validated.Temperature;
                    k.Enabled = validated.Enabled;
                });
            }
            catch (ArgumentException ex)
            {
                return AiSuiteEndpoints.Fail<object>(http, ex, "VALIDATION_ERROR");
            }

            if (req.Move is not null) store.MoveKey(id, req.Move);
            var final = store.GetKey(id)!;
            return AiSuiteEndpoints.Ok(Masked(final, store), http);
        })
        .WithName("AiSuite_UpdateLlmKey");

        settings.MapDelete("/llm-keys/{id}", (string id, HttpContext http,
            SettingsTokenService tokens, SettingsStore store) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            return store.DeleteKey(id)
                ? AiSuiteEndpoints.Ok(new { deleted = true }, http)
                : NotFound(http, "Key not found.");
        })
        .WithName("AiSuite_DeleteLlmKey");

        settings.MapPost("/llm-keys/{id}/test", async (string id, HttpContext http,
            SettingsTokenService tokens, SettingsStore store, AiSuiteOptions o,
            IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            var key = store.GetKey(id);
            if (key is null) return NotFound(http, "Key not found.");

            var sw = Stopwatch.StartNew();
            try
            {
                var adapter = LlmVendor.Create(
                    httpFactory.CreateClient(MultiLlmChatProvider.HttpClientName), key, o.Resilience);
                var reply = await adapter.ChatAsync(
                    new[] { new ChatMessage(ChatRole.User, "Reply with the single word OK.") },
                    new ChatRequestOptions(SystemPrompt: "Reply with exactly one word: OK.", MaxTokens: 16),
                    ct).ConfigureAwait(false);
                sw.Stop();
                store.MarkSuccess(id);
                var preview = reply.Content.Length > 120 ? reply.Content[..120] : reply.Content;
                return AiSuiteEndpoints.Ok(new
                {
                    ok = true, latencyMs = sw.ElapsedMilliseconds,
                    statusCode = (int?)null, model = reply.Model, message = preview
                }, http);
            }
            catch (LlmCallException ex)
            {
                sw.Stop();
                store.MarkFailure(id, ex.StatusNumber, ex.Message, ex.RetryAfterSeconds);
                return AiSuiteEndpoints.Ok(new
                {
                    ok = false, latencyMs = sw.ElapsedMilliseconds,
                    statusCode = ex.StatusNumber, model = (string?)null, message = SecretFree(ex.Message)
                }, http);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                return AiSuiteEndpoints.Ok(new
                {
                    ok = false, latencyMs = sw.ElapsedMilliseconds,
                    statusCode = (int?)null, model = (string?)null, message = SecretFree(ex.Message)
                }, http);
            }
        })
        .WithName("AiSuite_TestLlmKey")
        .WithSummary("Live round-trip test of one saved key (no secrets echoed).");

        // ── voice config ─────────────────────────────────────────────────
        settings.MapPut("/voice", (VoiceSettings req, HttpContext http,
            SettingsTokenService tokens, SettingsStore store) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            var allowed = new[] { VoiceSettings.Browser, VoiceSettings.OpenAiCompatible, VoiceSettings.Echo };
            if (!allowed.Contains(req.SttProvider ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"Unknown STT provider. Allowed: {string.Join(", ", allowed)}."),
                    "VALIDATION_ERROR");
            if (!allowed.Contains(req.TtsProvider ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"Unknown TTS provider. Allowed: {string.Join(", ", allowed)}."),
                    "VALIDATION_ERROR");

            // Blank/null keys mean "unchanged" — the UI only ever sees masked keys,
            // so an empty field must never wipe a previously stored secret.
            var existing = store.GetVoice();
            if (string.IsNullOrWhiteSpace(req.SttApiKey)) req.SttApiKey = existing.SttApiKey;
            if (string.IsNullOrWhiteSpace(req.TtsApiKey)) req.TtsApiKey = existing.TtsApiKey;

            store.SetVoice(req);
            var saved = store.GetVoice();
            return AiSuiteEndpoints.Ok(new
            {
                voice = new
                {
                    saved.SttEnabled, saved.SttProvider, saved.SttEndpoint, saved.SttModel,
                    sttKeyMasked = SettingsStore.MaskKey(saved.SttApiKey),
                    sttHasKey = !string.IsNullOrEmpty(saved.SttApiKey),
                    saved.TtsEnabled, saved.TtsProvider, saved.TtsEndpoint, saved.TtsModel,
                    saved.TtsVoice,
                    ttsKeyMasked = SettingsStore.MaskKey(saved.TtsApiKey),
                    ttsHasKey = !string.IsNullOrEmpty(saved.TtsApiKey)
                }
            }, http);
        })
        .WithName("AiSuite_SaveVoiceSettings");

        // ── knowledge-base uploads ───────────────────────────────────────
        settings.MapDelete("/kb/{id}", (string id, HttpContext http,
            SettingsTokenService tokens, SettingsStore store) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            return store.RemoveDocument(id)
                ? AiSuiteEndpoints.Ok(new { deleted = true }, http)
                : NotFound(http, "Document not found.");
        })
        .WithName("AiSuite_DeleteKbDocument");

        settings.MapPost("/kb", async (HttpRequest req, HttpContext http,
            SettingsTokenService tokens, SettingsStore store, AiSuiteOptions o, CancellationToken ct) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;

            if (!req.HasFormContentType)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException("Expected multipart/form-data with a 'file' part."), "VALIDATION_ERROR");

            var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException("No file uploaded. Allowed: .pdf, .docx, .md, .txt"), "VALIDATION_ERROR");
            if (file.Length > o.Settings.MaxUploadBytes)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"File exceeds the {o.Settings.MaxUploadBytes / (1024 * 1024)} MB limit."),
                    "VALIDATION_ERROR");

            try
            {
                using var stream = file.OpenReadStream();
                var parsed = DocumentParser.Parse(file.FileName, stream);
                var chunks = KnowledgeChunker.Chunk(parsed.Text)
                    .Select(t => new KnowledgeChunk
                    {
                        Text = t,
                        Keywords = KnowledgeChunker.Keywords(t)
                    })
                    .ToList();
                if (chunks.Count == 0)
                    throw new ArgumentException("The file produced no indexable text.");

                var doc = store.AddDocument(file.FileName, file.Length, chunks);
                return AiSuiteEndpoints.Ok(new
                {
                    document = doc,
                    characters = parsed.Text.Length,
                    warnings = parsed.Warnings
                }, http);
            }
            catch (ArgumentException ex)
            {
                return AiSuiteEndpoints.Fail<object>(http, ex, "VALIDATION_ERROR");
            }
            catch (InvalidDataException)
            {
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException("The file could not be parsed (corrupt archive)."), "VALIDATION_ERROR");
            }
        })
        .WithName("AiSuite_UploadKbDocument")
        .WithSummary("Upload a knowledge-base document (.pdf/.docx/.md/.txt) — parsed and chunked server-side.");

        // ── public runtime voice (only when enabled in settings) ─────────
        voice.MapPost("/stt", async (HttpRequest req, HttpContext http, IVoiceService voiceSvc,
            AiSuiteOptions o, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException("Expected multipart/form-data with a 'file' part."), "VALIDATION_ERROR");
            // Public route: cap the body BEFORE it is buffered (also covers chunked uploads).
            var maxAudio = o.Settings.MaxAudioBytes;
            if (req.ContentLength is { } cl && cl > maxAudio + 65536)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"Audio exceeds the {(maxAudio >= 1024 * 1024 ? (maxAudio / (1024 * 1024)) + " MB" : (maxAudio / 1024) + " KB")} limit."), "VALIDATION_ERROR");
            var maxFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (maxFeature is { IsReadOnly: false }) maxFeature.MaxRequestBodySize = maxAudio + 65536;
            IFormCollection form;
            try { form = await req.ReadFormAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException or IOException)
            {
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"Audio exceeds the {(maxAudio >= 1024 * 1024 ? (maxAudio / (1024 * 1024)) + " MB" : (maxAudio / 1024) + " KB")} limit or is malformed."), "VALIDATION_ERROR");
            }
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException("No audio file uploaded."), "VALIDATION_ERROR");
            if (file.Length > maxAudio)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"Audio exceeds the {(maxAudio >= 1024 * 1024 ? (maxAudio / (1024 * 1024)) + " MB" : (maxAudio / 1024) + " KB")} limit."), "VALIDATION_ERROR");
            var language = form["language"].FirstOrDefault();
            try
            {
                using var stream = file.OpenReadStream();
                var result = await voiceSvc.SpeechToTextAsync(stream, file.FileName, language, ct)
                    .ConfigureAwait(false);
                return AiSuiteEndpoints.Ok(new { text = result.Text, provider = result.Provider }, http);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or LlmCallException)
            {
                return AiSuiteEndpoints.Fail<object>(http, ex, "VOICE_UNAVAILABLE");
            }
        })
        .WithName("AiSuite_SpeechToText")
        .WithSummary("Speech-to-text. Active only when enabled in ⚙ Settings → Voice.");

        voice.MapPost("/tts", async (TtsRequestDto req, HttpContext http, IVoiceService voiceSvc,
            AiSuiteOptions o, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
                return AiSuiteEndpoints.Fail<object>(http, new ArgumentException("Text must not be empty."), "VALIDATION_ERROR");
            if (req.Text.Length > o.ChatDefaults.MaxMessageChars)
                return AiSuiteEndpoints.Fail<object>(http,
                    new ArgumentException($"Text exceeds {o.ChatDefaults.MaxMessageChars} characters."), "VALIDATION_ERROR");
            try
            {
                var result = await voiceSvc.TextToSpeechAsync(req.Text, req.Voice, ct).ConfigureAwait(false);
                return Results.File(result.Audio, result.ContentType);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or LlmCallException)
            {
                return AiSuiteEndpoints.Fail<object>(http, ex, "VOICE_UNAVAILABLE");
            }
        })
        .WithName("AiSuite_TextToSpeech")
        .WithSummary("Text-to-speech audio (raw bytes). Active only when enabled in ⚙ Settings → Voice.");

        // ── project service keys (shared /api/ai/v1 surface) ─────────────
        // Raw keys are returned ONCE here and are never retrievable again —
        // only the SHA-256 hash is stored. Keys are created per consumer
        // project; revocation is instant; rotation returns a new raw key.
        settings.MapPost("/service-keys", (CreateServiceKeyRequestDto req, HttpContext http,
            SettingsTokenService tokens, ServiceKeyStore keys) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(req.Name))
                return AiSuiteEndpoints.Fail<object>(http, new ArgumentException("Name the consumer project (e.g. \"billing-api\")."), "VALIDATION_ERROR");
            var (raw, info) = keys.Create(req.Name);
            return AiSuiteEndpoints.Ok(new
            {
                apiKey = raw, // shown ONCE — store it in the consumer's server config now
                key = info,
                message = "Store this key now: it is never shown again. Server-side use only — never ship it to a browser or mobile app."
            }, http);
        })
        .WithName("AiSuite_ServiceKeys_Create")
        .WithSummary("Issue a project API key for the shared /api/ai/v1 surface. Raw key shown once.");

        settings.MapGet("/service-keys", (HttpContext http, SettingsTokenService tokens, ServiceKeyStore keys) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            return AiSuiteEndpoints.Ok(keys.List(), http);
        })
        .WithName("AiSuite_ServiceKeys_List")
        .WithSummary("List project API keys (metadata only — never the raw key).");

        settings.MapPost("/service-keys/{id}/revoke", (string id, HttpContext http,
            SettingsTokenService tokens, ServiceKeyStore keys) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            return keys.Revoke(id)
                ? AiSuiteEndpoints.Ok(new { revoked = true }, http)
                : NotFound(http, "No service key with that id.");
        })
        .WithName("AiSuite_ServiceKeys_Revoke")
        .WithSummary("Revoke a project API key (instant).");

        settings.MapPost("/service-keys/{id}/rotate", (string id, HttpContext http,
            SettingsTokenService tokens, ServiceKeyStore keys) =>
        {
            if (Denied(http, tokens) is { } denied) return denied;
            var rotated = keys.Rotate(id);
            return rotated is null
                ? NotFound(http, "No service key with that id.")
                : AiSuiteEndpoints.Ok(new
                {
                    apiKey = rotated.Value.RawKey, // shown ONCE
                    key = rotated.Value.Info,
                    message = "The old key stopped working immediately. Store the new key now: it is never shown again."
                }, http);
        })
        .WithName("AiSuite_ServiceKeys_Rotate")
        .WithSummary("Rotate a project API key: old key dies instantly, new raw key shown once.");

        return app;
    }

    // ── helpers ──────────────────────────────────────────────────────────
    internal static string Token(HttpContext http)
    {
        var t = http.Request.Headers["X-Settings-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(t)) return t;
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        return string.Empty;
    }

    private static string Caller(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "local";

    private static IResult? Denied(HttpContext http, SettingsTokenService tokens) =>
        tokens.Validate(Token(http)) ? null : Unauthorized(http,
            "Settings session required. Enter the settings password first.");

    internal static IResult Unauthorized(HttpContext http, string message) =>
        Results.Json(new ApiResponse<object>(false, null, new ApiError("UNAUTHORIZED", message),
            AiSuiteEndpoints.TraceId(http)), statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotFound(HttpContext http, string message) =>
        Results.Json(new ApiResponse<object>(false, null, new ApiError("NOT_FOUND", message),
            AiSuiteEndpoints.TraceId(http)), statusCode: StatusCodes.Status404NotFound);

    private static object Masked(LlmKeyEntry k, SettingsStore store) => new
    {
        k.Id, k.Name, k.Vendor, k.Endpoint, k.Model,
        apiKeyMasked = SettingsStore.MaskKey(k.ApiKey),
        hasKey = !string.IsNullOrEmpty(k.ApiKey),
        k.MaxTokens, k.Temperature, k.Enabled, k.Priority,
        status = StatusOf(store, k.Id)
    };

    private static object StatusOf(SettingsStore store, string id)
    {
        var (state, until, lastStatus, lastError) = store.GetKeyStatus(id);
        return new { state, untilUtc = until, lastStatus, lastError };
    }

    /// <summary>
    /// Validates + normalizes an incoming key entry. Endpoint/model fall back
    /// to vendor defaults; API key is required for remote vendors (optional for
    /// localhost / Ollama).
    /// </summary>
    internal static LlmKeyEntry BuildEntry(
        string? name, string vendor, string? endpoint, string? model, string? apiKey,
        int? maxTokens, double? temperature, bool enabled)
    {
        vendor = string.IsNullOrWhiteSpace(vendor) ? "Custom" : vendor.Trim();
        var preset = LlmVendors.Find(vendor);
        var finalVendor = preset?.Vendor ?? vendor;
        var finalEndpoint = string.IsNullOrWhiteSpace(endpoint) ? preset?.Endpoint ?? string.Empty : endpoint.Trim();
        var finalModel = string.IsNullOrWhiteSpace(model) ? preset?.Model ?? string.Empty : model.Trim();

        if (string.IsNullOrWhiteSpace(finalEndpoint))
            throw new ArgumentException("Endpoint is required (e.g. https://api.openai.com/v1).");
        if (!Uri.TryCreate(finalEndpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint must be an absolute http(s) URL.");
        if (string.IsNullOrWhiteSpace(finalModel))
            throw new ArgumentException("Model is required (e.g. gpt-4o-mini).");

        var key = apiKey?.Trim() ?? string.Empty;
        var isLocal = uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        var keyOptional = isLocal || finalVendor.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
        if (key.Length == 0 && !keyOptional)
            throw new ArgumentException("API key is required for this vendor.");

        return new LlmKeyEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{finalVendor} key" : name.Trim(),
            Vendor = finalVendor,
            Endpoint = finalEndpoint,
            Model = finalModel,
            ApiKey = key,
            MaxTokens = maxTokens,
            Temperature = temperature,
            Enabled = enabled
        };
    }

    private static string SecretFree(string message) =>
        // Same sanitizer the health store uses: concise, key-redacted, no raw vendor JSON.
        Pixous.AI.Core.Services.ProviderErrorSanitizer.Sanitize(null, message);
}
