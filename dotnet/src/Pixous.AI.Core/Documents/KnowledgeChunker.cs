using System.Text.RegularExpressions;

namespace Pixous.AI.Core.Documents;

/// <summary>
/// Splits long text into retrievable passages and extracts keywords for
/// matching — used for uploaded knowledge-base documents. Zero dependencies.
/// </summary>
public static partial class KnowledgeChunker
{
    private const int TargetChars = 1000;

    /// <summary>Common-English stop words (not worth matching on).</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","are","but","not","you","all","any","can","had","her","was","one","our","out",
        "get","has","him","his","how","its","new","now","old","see","two","way","who","did","she","may",
        "use","what","when","where","which","there","these","than","then","them","they","this","that",
        "with","from","into","about","after","before","over","under","again","also","have","more","most",
        "some","such","only","other","very","just","because","while","during","through","each","both",
        "been","being","were","does","doing","will","would","could","should","shall","may","might","must",
        "here","why","how","what","when","who","whom","your","yours","my","me","i","he","it","is","in",
        "on","of","to","as","at","by","an","be","or","if","so","up","down","out","no","yes","do","does",
        "within","without","between","against","until","across","throughout","per","via","whether",
        "either","neither","every","another","onto","upon","also","just","only","even","still"
    };

    public static List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var paragraphs = Regex.Split(text.Trim(), @"\n{2,}|\r\n{2,}");
        var current = string.Empty;

        void Flush()
        {
            if (current.Trim().Length > 0) chunks.Add(current.Trim());
            current = string.Empty;
        }

        foreach (var raw in paragraphs)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;

            if (p.Length > TargetChars)
            {
                Flush();
                foreach (var piece in SplitLong(p)) chunks.Add(piece);
                continue;
            }

            if (current.Length + p.Length + 2 <= TargetChars)
            {
                current = current.Length == 0 ? p : current + "\n\n" + p;
            }
            else
            {
                Flush();
                current = p;
            }
        }
        Flush();
        return chunks;
    }

    private static IEnumerable<string> SplitLong(string text)
    {
        // Sentence-ish windows so no idea is cut in half.
        while (text.Length > TargetChars)
        {
            var cut = text.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, Math.Min(TargetChars, text.Length - 1));
            if (cut < TargetChars / 3) cut = text.LastIndexOf(' ', TargetChars);
            if (cut < TargetChars / 3) cut = TargetChars;
            var piece = text[..cut].Trim();
            if (piece.Length > 0) yield return piece;
            var rest = text[Math.Min(cut + 1, text.Length)..].TrimStart();
            if (rest.Length == 0) yield break;
            text = rest;
        }
        if (text.Trim().Length > 0) yield return text.Trim();
    }

    /// <summary>Top distinct keywords for a passage (frequency-ordered, stop-words removed).</summary>
    public static List<string> Keywords(string text, int max = 12)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tokenize(text))
        {
            if (t.Length < 3 || Stopwords.Contains(t)) continue;
            counts[t] = counts.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key.Length)
            .Take(max)
            .Select(kv => kv.Key.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    /// <summary>Lowercased alphanumeric tokens (length ≥ 3), stop-words removed.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in WordRe().Matches(text.ToLowerInvariant()))
        {
            var t = m.Value;
            if (t.Length < 3 || Stopwords.Contains(t)) continue;
            tokens.Add(t);
        }
        return tokens;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]{3,}")]
    private static partial Regex WordRe();
}
