namespace Pixous.AI.Core.Services;

/// <summary>
/// Live provider selection. Overrides the configured defaults at runtime
/// (no restart, no redeploy) — this is what a settings UI writes to.
/// Precedence everywhere: per-request provider → runtime selection → configuration.
/// API keys are NEVER handled here; they stay in User Secrets / env / Key Vault.
/// </summary>
public sealed class AiSuiteRuntimeSelection
{
    private readonly object _gate = new();
    private string? _chatProvider;
    private string? _translationProvider;

    public string? ChatProvider { get { lock (_gate) return _chatProvider; } }
    public string? TranslationProvider { get { lock (_gate) return _translationProvider; } }

    /// <summary>
    /// Validates names against the registered providers and applies them.
    /// Null/empty means "fall back to configuration". Returns an error message or null.
    /// </summary>
    public string? TrySet(
        string? chatProvider,
        string? translationProvider,
        IEnumerable<string> validChatProviders,
        IEnumerable<string> validTranslationProviders)
    {
        var chat = Normalize(chatProvider);
        var translation = Normalize(translationProvider);

        if (chat is not null && !Contains(validChatProviders, chat))
            return $"Unknown chat provider '{chat}'. Available: {string.Join(", ", validChatProviders)}.";
        if (translation is not null && !Contains(validTranslationProviders, translation))
            return $"Unknown translation provider '{translation}'. Available: {string.Join(", ", validTranslationProviders)}.";

        lock (_gate)
        {
            _chatProvider = chat;
            _translationProvider = translation;
        }
        return null;
    }

    public void Reset()
    {
        lock (_gate) { _chatProvider = null; _translationProvider = null; }
    }

    private static string? Normalize(string? v) =>
        string.IsNullOrWhiteSpace(v) || v.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null : v.Trim();

    private static bool Contains(IEnumerable<string> valid, string name) =>
        valid.Any(v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
}
