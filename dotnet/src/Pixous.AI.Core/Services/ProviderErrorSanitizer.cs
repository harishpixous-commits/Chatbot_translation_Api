using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pixous.AI.Core.Services;

/// <summary>
/// Turns a raw provider/vendor error into a concise operator-facing message.
/// Vendors may echo the caller's key or dump structured JSON into their error
/// bodies, so the raw text must never reach logs, health records or the UI
/// unfiltered: this redacts key-shaped tokens regardless of vendor behavior
/// and extracts just the human message instead of truncating raw JSON.
/// </summary>
public static partial class ProviderErrorSanitizer
{
    // Key-shaped tokens: vendor prefixes (sk-, pk_, rk-, xox...), explicit
    // key/value assignments (api_key=..., x-api-key: ...), Bearer headers, and
    // any long unbroken credential-looking run (24+ base64/hex-ish chars).
    [GeneratedRegex(@"(?i)\b(sk|pk|rk|xox[baprs]|ant|gsk|AIza|hf)[-_][A-Za-z0-9_\-]{4,}|(api[_-]?key|x-api-key|access[_-]?token|authorization)\s*[=:]\s*\S+|bearer\s+\S+|[A-Za-z0-9_\-+/=]{24,}")]
    private static partial Regex CredentialLike();

    public static string Sanitize(int? statusCode, string? raw)
    {
        var text = ExtractMessage(raw);
        text = CredentialLike().Replace(text, "[redacted]");
        text = text.Replace('{', ' ').Replace('}', ' ').Replace('"', ' ').Replace('\\', ' ');
        text = CollapseWhitespace().Replace(text, " ").Trim();
        if (statusCode is { } code && !text.Contains(code.ToString(), StringComparison.Ordinal))
            text = $"HTTP {code}. {text}";
        const int max = 160;
        return text.Length > max ? text[..max] : text;
    }

    // URL-shaped tokens: provider error bodies can leak internal
    // infrastructure addresses; clients never need them.
    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    /// <summary>
    /// Stricter variant for text that reaches the API CLIENT (e.g. SSE error
    /// frames): everything Sanitize does, plus URL redaction, and no status
    /// prefix (the frame carries a stable category code separately).
    /// </summary>
    public static string SanitizeForClient(string? raw)
    {
        var text = ExtractMessage(raw);
        text = CredentialLike().Replace(text, "[redacted]");
        text = UrlLike().Replace(text, "[redacted-url]");
        text = text.Replace('{', ' ').Replace('}', ' ').Replace('"', ' ').Replace('\\', ' ');
        text = CollapseWhitespace().Replace(text, " ").Trim();
        const int max = 160;
        return text.Length > max ? text[..max] : text;
    }

    /// <summary>Pulls the nested "message" field out of a vendor JSON body; otherwise returns the raw text.</summary>
    private static string ExtractMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Provider call failed.";
        var jsonStart = raw.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw[jsonStart..]);
                var found = FindMessage(doc.RootElement);
                if (found is { Length: > 0 })
                {
                    var prefix = raw[..jsonStart].Trim();
                    return prefix.Length > 0 ? $"{prefix} {found}" : found;
                }
            }
            catch (JsonException) { /* not parseable - fall through to raw text */ }
        }
        return raw;
    }

    private static string? FindMessage(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.NameEquals("message") && prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
                var nested = FindMessage(prop.Value);
                if (nested is not null) return nested;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var nested = FindMessage(item);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
