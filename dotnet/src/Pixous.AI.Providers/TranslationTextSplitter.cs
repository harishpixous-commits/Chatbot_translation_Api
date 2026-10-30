namespace Pixous.AI.Providers;

/// <summary>
/// Splits one long text into ordered pieces of at most <c>max</c> chars for engines with a per-item cap.
/// Pieces end after a sentence terminator (. ! ? U+0964 U+0965) plus following whitespace or after a newline;
/// a single sentence longer than the cap is cut at the last whitespace inside the cap, or hard-cut (never
/// inside a surrogate pair) when it has none. Concatenating the pieces reproduces the input exactly.
/// Sentences are translated independently, so cross-sentence context is lost at piece boundaries.
/// </summary>
internal static class TranslationTextSplitter
{
    public static List<string> Split(string text, int max)
    {
        if (max < 2) throw new ArgumentOutOfRangeException(nameof(max));
        var pieces = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= max) { pieces.Add(text[start..]); break; }
            var limit = start + max;                       // exclusive end bound
            var cut = LastSentenceEnd(text, start, limit);
            if (cut <= start) cut = LastWhitespaceEnd(text, start, limit);
            if (cut <= start)
            {
                cut = limit;
                if (char.IsHighSurrogate(text[cut - 1])) cut--;
            }
            pieces.Add(text[start..cut]);
            start = cut;
        }
        return pieces;
    }

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '\u0964' or '\u0965';

    // End index (exclusive) of the last sentence boundary within [start, limit): terminator followed by
    // whitespace (whitespace included in the piece), or a newline.
    private static int LastSentenceEnd(string t, int start, int limit)
    {
        for (var i = limit - 1; i > start; i--)
        {
            if (t[i] == '\n') return i + 1;
            if (char.IsWhiteSpace(t[i]) && IsTerminator(t[i - 1]))
            {
                var j = i;
                while (j + 1 < limit && char.IsWhiteSpace(t[j + 1])) j++;
                return j + 1;
            }
        }
        return -1;
    }

    private static int LastWhitespaceEnd(string t, int start, int limit)
    {
        for (var i = limit - 1; i > start; i--)
            if (char.IsWhiteSpace(t[i])) return i + 1;
        return -1;
    }
}
