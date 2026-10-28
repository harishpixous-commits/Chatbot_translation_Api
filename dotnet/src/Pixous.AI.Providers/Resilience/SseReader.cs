using System.Text;

namespace Pixous.AI.Providers.Resilience;

/// <summary>
/// Minimal Server-Sent Events reader: yields each `data:` payload (one per
/// event; multi-line data is joined with \n) until EOF or an explicit
/// terminator payload ("[DONE]"). Vendor-agnostic.
/// </summary>
public static class SseReader
{
    /// <param name="maxPayloadBytes">
    /// Hard cap on one SSE event's accumulated data (default 1 MB). A buggy or
    /// malicious upstream that never sends a blank line would otherwise grow
    /// memory without bound. Exceeding the cap throws InvalidDataException.
    /// </param>
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        string? terminator = "[DONE]",
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        int maxPayloadBytes = 1_000_000)
    {
        var accumulated = 0;
        var sawTerminator = false;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var dataLines = new List<string>(1);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                // Blank line = event boundary.
                if (dataLines.Count > 0)
                {
                    var payload = string.Join("\n", dataLines);
                    dataLines.Clear();
                    accumulated = 0;
                    if (terminator is not null && payload == terminator) { sawTerminator = true; yield break; }
                    yield return payload;
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var v = line[5..];
                if (v.StartsWith(' ')) v = v[1..];
                accumulated += v.Length + 1;
                if (accumulated > maxPayloadBytes)
                    throw new InvalidDataException(
                        $"SSE event exceeded the {maxPayloadBytes}-byte payload cap; aborting the stream.");
                dataLines.Add(v);
            }
            // "event:", "id:", "retry:" and comments (":") are ignored —
            // event TYPES that matter (Anthropic) are inside the JSON payload.
        }
        // Flush a trailing event without a blank-line terminator.
        if (dataLines.Count > 0)
        {
            var payload = string.Join("\n", dataLines);
            if (terminator is not null && payload == terminator) sawTerminator = true;
            else yield return payload;
        }
        // Fail closed: a well-behaved OpenAI-compatible upstream always ends with
        // the terminator. EOF without it means the connection died mid-stream
        // (observed: llama-cpp-python aborts the body when prompt exceeds the
        // context window) - never present that as a completed, empty reply.
        if (terminator is not null && !sawTerminator)
            throw new InvalidDataException(
                $"Upstream SSE stream ended without the expected '{terminator}' terminator; the connection was lost mid-stream.");
    }
}
