namespace Pixous.AI.Providers;

/// <summary>
/// An LLM HTTP call failed with a status code. Lets the fallback chain
/// classify failures per key: 429 → exhausted (cool-down), 401/403 → invalid
/// key, other 4xx → bad config for that key, 5xx/network → transient.
/// Message is secret-free (status + truncated upstream body).
/// </summary>
public sealed class LlmCallException : HttpRequestException
{
    /// <summary>Raw Retry-After value when the upstream sent one (seconds or HTTP-date).</summary>
    public string? RetryAfter { get; }

    public LlmCallException(int statusCode, string message, string? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusNumber = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status as a plain int (for classification + JSON).</summary>
    public int? StatusNumber { get; }

    /// <summary>Normalized seconds if Retry-After is numeric, else null.</summary>
    public int? RetryAfterSeconds =>
        RetryAfter is not null && int.TryParse(RetryAfter, out var s) && s > 0 ? s : null;
}
