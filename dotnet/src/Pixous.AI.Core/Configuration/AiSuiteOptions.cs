namespace Pixous.AI.Core.Configuration;

// ── Root ─────────────────────────────────────────────────────────────────
/// <summary>
/// Centralized, strongly-typed configuration (section <c>"AiSuite"</c>).
/// NON-SECRET settings only: provider names, endpoints, models, languages,
/// resilience/rate-limit/security toggles. Every secret is referenced via an
/// <c>*ApiKeyConfigKey</c> pointer and resolved from User Secrets / env vars /
/// Key Vault at startup — never bound here, never logged, never returned.
/// </summary>
public sealed class AiSuiteOptions
{
    public const string SectionName = "AiSuite";

    /// <summary>Active chat provider (e.g. "KnowledgeBase", "Echo", "OpenAiCompatible").</summary>
    public string ChatProvider { get; set; } = "Echo";

    /// <summary>Active translation provider (e.g. "Echo", "AzureTranslator", "LibreTranslate").</summary>
    public string TranslationProvider { get; set; } = "Echo";

    public OpenAiCompatibleOptions OpenAiCompatible { get; set; } = new();
    public AzureTranslatorOptions AzureTranslator { get; set; } = new();
    public LibreTranslateOptions LibreTranslate { get; set; } = new();
    public PixousEngineOptions PixousEngine { get; set; } = new();
    public KnowledgeBaseOptions KnowledgeBase { get; set; } = new();
    public ChatDefaultsOptions ChatDefaults { get; set; } = new();
    public SupportedLanguagesOptions Languages { get; set; } = new();
    public ResilienceOptions Resilience { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
    public DataProtectionOptions DataProtection { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
    public SettingsOptions Settings { get; set; } = new();

    /// <summary>Validates the options. Returns error messages (empty = valid).</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ChatProvider))
            yield return $"{nameof(ChatProvider)} must be set.";
        if (string.IsNullOrWhiteSpace(TranslationProvider))
            yield return $"{nameof(TranslationProvider)} must be set.";

        if (ChatProvider.Equals("OpenAiCompatible", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(OpenAiCompatible.Endpoint))
                yield return "OpenAiCompatible:Endpoint must be set when ChatProvider is OpenAiCompatible.";
        }
        if (OpenAiCompatible.TimeoutSeconds is < 1)
            yield return "OpenAiCompatible:TimeoutSeconds must be >= 1 when set.";
        if (TranslationProvider.Equals("AzureTranslator", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(AzureTranslator.Endpoint))
                yield return "AzureTranslator:Endpoint must be set when TranslationProvider is AzureTranslator.";
            if (string.IsNullOrWhiteSpace(AzureTranslator.Region))
                yield return "AzureTranslator:Region must be set when TranslationProvider is AzureTranslator.";
        }
        if (TranslationProvider.Equals("LibreTranslate", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(LibreTranslate.Endpoint))
                yield return "LibreTranslate:Endpoint must be set when TranslationProvider is LibreTranslate.";
        }
        if (TranslationProvider.Equals("PixousSelfHosted", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(PixousEngine.Endpoint))
                yield return "PixousEngine:Endpoint must be set when TranslationProvider is PixousSelfHosted.";
        }
        foreach (var e in Languages.Validate()) yield return e;
        foreach (var e in Resilience.Validate()) yield return e;
        foreach (var e in RateLimit.Validate()) yield return e;
        foreach (var e in Security.Validate()) yield return e;
        foreach (var e in Cors.Validate()) yield return e;
        foreach (var e in Settings.Validate()) yield return e;
    }
}

// ── Chat ─────────────────────────────────────────────────────────────────
/// <summary>Any OpenAI-compatible chat endpoint (OpenAI, Azure /openai/v1, Ollama, LM Studio, vLLM...).</summary>
public sealed class OpenAiCompatibleOptions
{
    /// <summary>Chat-completions base URL, e.g. https://api.openai.com/v1</summary>
    public string? Endpoint { get; set; }
    /// <summary>Model / deployment name, swappable per environment.</summary>
    public string Model { get; set; } = "gpt-4o-mini";
    public int MaxTokens { get; set; } = 512;
    public double Temperature { get; set; } = 0.7;
    /// <summary>Config path holding the secret — NOT the secret itself. Default: "AiSuite:ApiKeys:Chat".</summary>
    public string ApiKeyConfigKey { get; set; } = "AiSuite:ApiKeys:Chat";
    public bool RequireApiKey { get; set; } = true;
    /// <summary>
    /// Per-provider call timeout; null (default) inherits Resilience:TimeoutSeconds.
    /// Local CPU inference (llama.cpp, Ollama) legitimately exceeds 60s on low-core
    /// machines — a short reply from Qwen3-4B Q4_K_M measured 85.5s on a 2-vCPU
    /// Windows CI runner. Hosted vendors should usually inherit the global value:
    /// it is a billing guard, and a timed-out paid call is never auto-retried.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
    /// <summary>
    /// Strip &lt;think&gt;...&lt;/think&gt; reasoning blocks some local models
    /// (e.g. Qwen3) embed in chat content. Handles tags split across streaming
    /// chunks; text outside think blocks is preserved. Default true.
    /// </summary>
    public bool StripThinkTags { get; set; } = true;
}

public sealed class ChatDefaultsOptions
{
    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    public int MaxHistoryMessages { get; set; } = 20;
    public int MaxMessageChars { get; set; } = 4000;
}

// ── Translation ──────────────────────────────────────────────────────────
public sealed class AzureTranslatorOptions
{
    public string? Endpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";
    public string? Region { get; set; }
    /// <summary>Config path holding the subscription key — NOT the key itself.</summary>
    public string ApiKeyConfigKey { get; set; } = "AiSuite:ApiKeys:Translator";
}

public sealed class LibreTranslateOptions
{
    public string? Endpoint { get; set; }
    /// <summary>Optional config path for the LibreTranslate key (usually null; self-hosted needs none).</summary>
    public string? ApiKeyConfigKey { get; set; }
}

/// <summary>Self-hosted Pixous engine sidecar (IndicTrans2 CT2). No external provider is contacted.</summary>
public sealed class PixousEngineOptions
{
    /// <summary>Sidecar base URL, e.g. http://127.0.0.1:8765. Localhost default; the sidecar's bind is not auth — see engine-sidecar README before network exposure.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Optional config path for the sidecar bearer token (matches PIXOUS_ENGINE_TOKEN). Null = no token.</summary>
    public string? TokenConfigKey { get; set; }
}

// ── Languages ────────────────────────────────────────────────────────────
/// <summary>Allow-list of language codes the API accepts (abuse + typo control).</summary>
public sealed class SupportedLanguagesOptions
{
    public string Default { get; set; } = "en";
    public List<string> Supported { get; set; } = new() { "en" };

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Default))
            yield return "Languages:Default must be set.";
        if (Supported.Count == 0)
            yield return "Languages:Supported must contain at least one language.";
        if (!Supported.Contains(Default))
            yield return "Languages:Supported must contain the Default language.";
    }

    public bool IsSupported(string code) =>
        Supported.Contains(code, StringComparer.OrdinalIgnoreCase);
}

// ── Knowledge base (config-driven FAQ answers) ───────────────────────────
/// <summary>One FAQ entry: phrases to match + the answer to return.</summary>
public sealed class KnowledgeEntry
{
    public List<string> Match { get; set; } = new();
    public string Answer { get; set; } = string.Empty;
}

public sealed class KnowledgeBaseOptions
{
    public List<KnowledgeEntry> Entries { get; set; } = new();
    public string FallbackAnswer { get; set; } =
        "I don't have an answer for that yet. Try asking about our services, products, contact or careers.";
}

// ── Resilience ───────────────────────────────────────────────────────────
/// <summary>Timeout + retry for all provider HTTP calls. Retries apply to
/// transient failures only (5xx, 429, 408, timeouts) — never to 4xx/auth errors.</summary>
public sealed class ResilienceOptions
{
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>
    /// When false (default), a provider call that may already have been billed is
    /// NEVER retried: a timeout, 5xx or network failure can mean the provider
    /// processed the prompt. Only definitive rejections (HTTP 429) are retried.
    /// Set true only when duplicate charges are acceptable (e.g. free/local).
    /// </summary>
    public bool RetryAmbiguousFailures { get; set; } = false;

    /// <summary>
    /// When false (default), the MultiLLM chain falls through to the next key only
    /// after a definitive rejection (4xx). After an ambiguous failure (timeout,
    /// 5xx, network) it stops with a clear error instead of re-sending the same
    /// prompt to another vendor, which could double-bill. Opt-in for free/local
    /// keys or accepted risk.
    /// </summary>
    public bool AllowAmbiguousKeyFallback { get; set; } = false;

    /// <summary>
    /// When false (default), an HTTP 429 (rate limit / quota exhausted) does
    /// NOT fail over to the next configured key: the vendor's quota answer is
    /// honored and surfaced (with Retry-After when given). Failing over would
    /// spend a DIFFERENT key's quota — that is quota evasion, not
    /// availability. Set true only when the operator deliberately wants
    /// multi-key quota pooling.
    /// </summary>
    public bool AllowQuotaKeyFallback { get; set; } = false;

    public IEnumerable<string> Validate()
    {
        if (TimeoutSeconds < 1) yield return "Resilience:TimeoutSeconds must be >= 1.";
        if (MaxRetries is < 0 or > 5) yield return "Resilience:MaxRetries must be 0-5.";
        if (BaseDelayMs < 0) yield return "Resilience:BaseDelayMs must be >= 0.";
    }
}

// ── Rate limiting (host-level, built-in, no extra packages) ──────────────
public sealed class RateLimitOptions
{
    /// <summary>
    /// ON by default since 1.0.1 (safe default): the public chat/translation/voice
    /// endpoints spend the operator's provider quota on every call, so out of the
    /// box each caller IP is capped. Set <c>Enabled=false</c> only when the host
    /// already front-ends its own limiter/WAF. The settings surface (login etc.)
    /// keeps its own brute-force lockout and is not covered by this limiter.
    /// </summary>
    public bool Enabled { get; set; } = true;
    public int PermitLimit { get; set; } = 20;
    public int WindowSeconds { get; set; } = 60;

    public IEnumerable<string> Validate()
    {
        if (!Enabled) yield break;
        if (PermitLimit < 1) yield return "RateLimit:PermitLimit must be >= 1 when enabled.";
        if (WindowSeconds < 1) yield return "RateLimit:WindowSeconds must be >= 1 when enabled.";
    }
}

// ── Data Protection (key ring for settings encryption at rest) ──────────
public sealed class DataProtectionOptions
{
    /// <summary>
    /// Optional explicit directory for the ASP.NET Core Data Protection key ring
    /// (the master keys that encrypt persisted settings). WHEN SET, the library
    /// creates it with owner-only permissions (0700, key files 0600 on Unix) and
    /// warns at startup if it sits inside Settings:StoreDirectory — master keys
    /// must never live beside the secrets they encrypt. WHEN NULL, the framework
    /// default location is used (user profile / container-local): fine for a
    /// single persistent host, but multi-instance or ephemeral hosts MUST set a
    /// shared, backed-up location (or PersistKeysToStackExchangeRedis / blob /
    /// Key Vault via the host's own AddDataProtection) or stored keys become
    /// undecryptable on restart (they then show as "locked", never lost).
    /// </summary>
    public string? KeyRingDirectory { get; set; }
}

// ── Security toggles ─────────────────────────────────────────────────────
public sealed class SecurityOptions
{
    /// <summary>
    /// Require an authenticated caller on all AI endpoints. Off by default so the
    /// sample runs standalone. Two ways to satisfy it: the host registers its own
    /// JWT/OIDC scheme via AddAuthentication, OR <see cref="ApiKeys"/> is set and
    /// the module's built-in "PixousApiKey" scheme (X-API-Key header) is used.
    /// Startup fails closed when this is on with no scheme available.
    /// </summary>
    public bool RequireAuthorization { get; set; } = false;

    /// <summary>
    /// Optional API keys for the built-in "PixousApiKey" scheme. Load from
    /// User Secrets / env vars / Key Vault (e.g. AiSuite__Security__ApiKeys__0) —
    /// never commit to source. Compared in constant time; never logged, never
    /// echoed in responses.
    /// </summary>
    public string[] ApiKeys { get; set; } = Array.Empty<string>();

    public IEnumerable<string> Validate()
    {
        for (var i = 0; i < ApiKeys.Length; i++)
            if (string.IsNullOrEmpty(ApiKeys[i]) || ApiKeys[i].Length < 16)
                yield return $"Security:ApiKeys[{i}] must be at least 16 characters.";
    }
}

// ── CORS (exact-origin only, off by default) ─────────────────────────────
public sealed class CorsOptions
{
    /// <summary>
    /// Off by default: same-origin self-hosted deployments need no CORS headers
    /// at all (browsers don't ask). Enable only for cross-origin browser frontends.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Exact origins (scheme://host[:port]). Wildcards are rejected by validation:
    /// with '*' the browser sends every visitor's Origin through, which defeats
    /// the point for a quota-spending API.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Allow cookies/credentials cross-origin. Rarely needed; the API-key header is not a credential.</summary>
    public bool AllowCredentials { get; set; } = false;

    public IEnumerable<string> Validate()
    {
        if (!Enabled) yield break;
        if (AllowedOrigins.Length == 0)
            yield return "Cors:AllowedOrigins must list at least one exact origin when Cors:Enabled is true.";
        foreach (var o in AllowedOrigins)
        {
            if (o == "*")
                yield return "Cors:AllowedOrigins must be exact origins, never '*'.";
            else if (!Uri.TryCreate(o, UriKind.Absolute, out var u)
                     || (u.Scheme != "https" && u.Scheme != "http")
                     || u.AbsolutePath.Trim('/').Length != 0)
                yield return $"Cors:AllowedOrigins entry '{o}' must be an absolute origin (scheme://host[:port], no path).";
        }
    }
}
