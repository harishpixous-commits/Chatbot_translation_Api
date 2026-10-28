namespace Pixous.AI.Core.Configuration;

// ── Runtime Settings panel (password-gated, file-backed) ──────────────────
/// <summary>
/// Options for the admin Settings surface (⚙ panel): password gate, storage
/// location and key-fallback behaviour. NON-SECRET defaults live here; the
/// admin password can be overridden via User Secrets / env
/// (<c>AiSuite__Settings__Password</c>) and is superseded once changed in the UI
/// (stored as a PBKDF2 hash).
/// </summary>
public sealed class SettingsOptions
{
    /// <summary>Map the /api/ai/settings endpoints at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Initial admin password for the Settings panel (change via UI).</summary>
    public string Password { get; set; } = "pixous";

    /// <summary>
    /// Store directory — relative to the host content root, or rooted.
    /// Holds settings.json (keys/voice/index) + kb/ (uploaded documents).
    /// </summary>
    public string StoreDirectory { get; set; } = "ai-suite-settings";

    /// <summary>
    /// Persist API keys to disk (settings.json). Off = keys live in memory only
    /// and are lost on restart. ON IS A DEMO CONVENIENCE — production should
    /// keep keys in User Secrets / env / Key Vault and leave this off.
    /// </summary>
    public bool AllowPersistKeys { get; set; } = true;

    /// <summary>Lifetime of the settings session token.</summary>
    public int TokenHours { get; set; } = 8;

    /// <summary>Cool-down applied to a key after HTTP 429 (quota / rate limit) before it is retried.</summary>
    public int ExhaustedCooldownSeconds { get; set; } = 60;

    /// <summary>Cool-down applied to a key after HTTP 401/403 (invalid key).</summary>
    public int InvalidKeyCooldownHours { get; set; } = 24;

    /// <summary>Cool-down after a key-level 4xx (bad endpoint/model for that key).</summary>
    public int BadConfigCooldownMinutes { get; set; } = 10;

    /// <summary>Max upload size for knowledge-base documents.</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;

    public int MinPasswordLength { get; set; } = 8;

    public IEnumerable<string> Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Password) && Password is not null)
            yield return "Settings:Password must not be empty when Settings are enabled.";
        if (string.IsNullOrWhiteSpace(StoreDirectory))
            yield return "Settings:StoreDirectory must be set.";
        if (TokenHours < 1)
            yield return "Settings:TokenHours must be >= 1.";
        if (ExhaustedCooldownSeconds < 1)
            yield return "Settings:ExhaustedCooldownSeconds must be >= 1.";
        if (MinPasswordLength < 4)
            yield return "Settings:MinPasswordLength must be >= 4.";
        if (MaxUploadBytes < 1024)
            yield return "Settings:MaxUploadBytes must be >= 1 KB.";
    }
}

// ── Saved LLM API key entry (settings store) ─────────────────────────────
/// <summary>
/// One user-saved LLM API configuration. ANY vendor — adapter chosen by
/// <see cref="Vendor"/>. The key itself is a secret: stored only in the
/// settings store (never in appsettings, never returned by any API — responses
/// carry a masked form only).
/// </summary>
public sealed class LlmKeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;

    /// <summary>Vendor preset id, e.g. "OpenAI", "Anthropic", "Gemini", "Ollama", "Custom".</summary>
    public string Vendor { get; set; } = "Custom";

    /// <summary>Base URL (adapter appends its own path). Empty = vendor default.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }

    /// <summary>Extra HTTP headers (e.g. OpenAI-Organization) — non-secret extras only.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Lower = tried first by the fallback chain. Maintained by the store.</summary>
    public int Priority { get; set; }

    /// <summary>When the vendor first accepted this key (test or real traffic). Null = never verified -
    /// the UI must show "untested", not "ok": an unverified key says nothing about validity.</summary>
    public DateTime? VerifiedUtc { get; set; }

    public LlmKeyEntry Clone() => new()
    {
        Id = Id, Name = Name, Vendor = Vendor, Endpoint = Endpoint, Model = Model,
        ApiKey = ApiKey, MaxTokens = MaxTokens, Temperature = Temperature, VerifiedUtc = VerifiedUtc,
        Headers = new Dictionary<string, string>(Headers), Enabled = Enabled, Priority = Priority
    };
}

/// <summary>Vendor presets (single source of truth for the settings UI).</summary>
public sealed record LlmVendorPreset(string Vendor, string Label, string Endpoint, string Model, bool RequiresKey);

public static class LlmVendors
{
    public static readonly IReadOnlyList<LlmVendorPreset> All = new[]
    {
        new LlmVendorPreset("OpenAI",       "OpenAI",            "https://api.openai.com/v1",                   "gpt-4o-mini",        true),
        new LlmVendorPreset("Anthropic",    "Anthropic Claude",  "https://api.anthropic.com",                   "claude-3-5-haiku-latest", true),
        new LlmVendorPreset("Gemini",       "Google Gemini",     "https://generativelanguage.googleapis.com/v1beta", "gemini-2.0-flash", true),
        new LlmVendorPreset("DeepSeek",     "DeepSeek",          "https://api.deepseek.com/v1",                 "deepseek-chat",      true),
        new LlmVendorPreset("Groq",         "Groq",              "https://api.groq.com/openai/v1",              "llama-3.3-70b-versatile", true),
        new LlmVendorPreset("Mistral",      "Mistral",           "https://api.mistral.ai/v1",                   "mistral-small-latest", true),
        new LlmVendorPreset("OpenRouter",   "OpenRouter",        "https://openrouter.ai/api/v1",                "openai/gpt-4o-mini", true),
        new LlmVendorPreset("Ollama",       "Ollama (local)",    "http://localhost:11434/v1",                   "llama3.2",           false),
        new LlmVendorPreset("Custom",       "Custom / other OpenAI-compatible", "",                          "",                   false),
    };

    public static LlmVendorPreset? Find(string vendor) =>
        All.FirstOrDefault(p => p.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase));
}

// ── Voice (speech-to-text + text-to-speech) settings ─────────────────────
/// <summary>
/// Voice configuration. <c>Browser</c> providers run entirely in the client
/// (Web Speech API — free, no key). <c>OpenAiCompatible</c> providers call
/// server-side /audio/* endpoints with the saved key. <c>Echo</c> = keyless
/// demo behaviour.
/// </summary>
public sealed class VoiceSettings
{
    public const string Browser = "Browser";
    public const string OpenAiCompatible = "OpenAiCompatible";
    public const string Echo = "Echo";

    public bool SttEnabled { get; set; }
    public string SttProvider { get; set; } = Browser;
    public string SttEndpoint { get; set; } = "https://api.openai.com/v1";
    public string SttModel { get; set; } = "whisper-1";
    public string SttApiKey { get; set; } = string.Empty;

    public bool TtsEnabled { get; set; }
    public string TtsProvider { get; set; } = Browser;
    public string TtsEndpoint { get; set; } = "https://api.openai.com/v1";
    public string TtsModel { get; set; } = "tts-1";
    public string TtsVoice { get; set; } = "alloy";
    public string TtsApiKey { get; set; } = string.Empty;

    public VoiceSettings Clone() => (VoiceSettings)MemberwiseClone();
}

// ── Uploaded knowledge documents ─────────────────────────────────────────
/// <summary>Index entry for one uploaded knowledge-base document.</summary>
public sealed class KnowledgeDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One retrievable passage of an uploaded document (persisted per document).</summary>
public sealed class KnowledgeChunk
{
    public string Text { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
}

/// <summary>Search result from the knowledge store (FAQ entries + uploads).</summary>
public sealed record KnowledgeHit(string Source, string Text, double Score);
