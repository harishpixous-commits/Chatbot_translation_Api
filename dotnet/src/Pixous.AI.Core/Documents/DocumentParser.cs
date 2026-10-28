using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Pixous.AI.Core.Documents;

/// <summary>Result of parsing an uploaded document.</summary>
public sealed record ParsedDocument(string Text, IReadOnlyList<string> Warnings);

/// <summary>
/// Text extraction for knowledge-base uploads — PDF, DOCX, Markdown, TXT —
/// with ZERO third-party packages (framework only):
///   .md/.txt  → read as-is
///   .docx     → ZipArchive + word/document.xml (&lt;w:t&gt; runs)
///   .pdf      → built-in content-stream text extraction (FlateDecode inflate +
///               Tj/TJ/' string operators, WinAnsi/Latin-1 best effort)
/// Scanned/image PDFs have no text layer — a warning is returned instead.
/// </summary>
public static partial class DocumentParser
{
    public static readonly string[] AllowedExtensions = { ".pdf", ".docx", ".md", ".txt" };

    public static ParsedDocument Parse(string fileName, Stream stream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".txt" => ReadText(stream, ext),
            ".docx" => ParseDocx(stream),
            ".pdf" => ParsePdf(stream),
            _ => throw new ArgumentException(
                $"Unsupported file type '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}.")
        };
    }

    // ── .md / .txt ───────────────────────────────────────────────────────
    private static ParsedDocument ReadText(Stream stream, string ext)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd().Replace("\r\n", "\n").Trim();
        if (text.Length == 0)
            throw new ArgumentException("The file contains no text.");
        return new ParsedDocument(text, Array.Empty<string>());
    }

    // ── .docx ────────────────────────────────────────────────────────────
    private static ParsedDocument ParseDocx(Stream stream)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new ArgumentException("Not a valid .docx file."); }

        using (zip)
        {
            var entry = zip.GetEntry("word/document.xml")
                ?? throw new ArgumentException("Not a valid .docx file (word/document.xml missing).");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();

            // Paragraph/line/tab breaks first, then pull the <w:t> runs in order.
            var sb = new StringBuilder();
            var rx = DocxRx();
            foreach (Match m in rx.Matches(xml))
            {
                switch (m.Value)
                {
                    case var v when v.StartsWith("</w:p", StringComparison.Ordinal):
                        sb.Append('\n');
                        break;
                    case var v when v.StartsWith("<w:br", StringComparison.Ordinal) || v.StartsWith("<w:cr", StringComparison.Ordinal):
                        sb.Append('\n');
                        break;
                    case var v when v.StartsWith("<w:tab", StringComparison.Ordinal):
                        sb.Append('\t');
                        break;
                    default:
                        sb.Append(System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : string.Empty));
                        break;
                }
            }

            var text = Normalize(sb.ToString());
            if (text.Length == 0)
                throw new ArgumentException("The document contains no extractable text.");
            return new ParsedDocument(text, Array.Empty<string>());
        }
    }

    [GeneratedRegex(@"</w:p[^>]*>|<w:br[^>]*/>|<w:cr[^>]*/>|<w:tab[^>]*/>|<w:t(?:\s[^>]*)?>(.*?)</w:t>", RegexOptions.Singleline)]
    private static partial Regex DocxRx();

    private static string Normalize(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());
        var joined = string.Join("\n", lines);
        return Regex.Replace(joined, @"\n{3,}", "\n\n").Trim();
    }

    // ── .pdf ─────────────────────────────────────────────────────────────
    private static ParsedDocument ParsePdf(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        if (bytes.Length < 4 || bytes[0] != '%' || bytes[1] != 'P')
            throw new ArgumentException("Not a valid PDF file.");

        var warnings = new List<string>();
        var sb = new StringBuilder();

        foreach (var content in ExtractContentStreams(bytes))
            AppendTextOps(content, sb);

        var text = Normalize(sb.ToString());
        if (text.Length < 10)
        {
            // Common for scans/photos of pages — nothing to index.
            warnings.Add("No extractable text found — this PDF may be a scan or image-only. " +
                         "Upload a text-based PDF, .docx or .md instead.");
            text = string.Empty;
        }
        return new ParsedDocument(text, warnings);
    }

    /// <summary>Finds stream...endstream blobs and returns inflated content streams (best effort).</summary>
    private static IEnumerable<string> ExtractContentStreams(byte[] bytes)
    {
        var latin1 = Encoding.Latin1;
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");
        var start = 0;

        while (true)
        {
            var m = IndexOf(bytes, marker, start);
            if (m < 0) yield break;
            var dataStart = m + marker.Length;
            // Skip the single EOL after "stream".
            if (dataStart < bytes.Length && bytes[dataStart] == (byte)'\r') dataStart++;
            if (dataStart < bytes.Length && bytes[dataStart] == (byte)'\n') dataStart++;

            var end = IndexOf(bytes, endMarker, dataStart);
            if (end < 0) yield break;
            // "endstream" is preceded by an EOL that belongs to the stream data.
            var dataEnd = end;
            if (dataEnd > dataStart && bytes[dataEnd - 1] == (byte)'\n') dataEnd--;
            if (dataEnd > dataStart && bytes[dataEnd - 1] == (byte)'\r') dataEnd--;

            if (dataEnd > dataStart)
            {
                var slice = new byte[dataEnd - dataStart];
                Array.Copy(bytes, dataStart, slice, 0, slice.Length);
                // Uncompressed content stream (rare but valid):
                var raw = latin1.GetString(slice);
                if (LooksLikeContent(raw)) yield return raw;

                // FlateDecode (the common case): try zlib, then raw deflate.
                var inflated = Inflate(slice);
                if (inflated is not null && LooksLikeContent(inflated)) yield return inflated;
            }

            start = end + endMarker.Length;
        }
    }

    private static bool LooksLikeContent(string s) =>
        s.Contains("BT") && (s.Contains("Tj") || s.Contains("TJ") || s.Contains("Td") || s.Contains("T*"));

    private static string? Inflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch (Exception e) when (e is InvalidDataException or IOException) { }

        try
        {
            using var input = new MemoryStream(data);
            using var z = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException) { }

        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    /// <summary>
    /// Walks a PDF content stream and materializes text from the show operators:
    /// <c>(str) Tj</c>, <c>(str) '</c>, <c>[(a) -120 (b)] TJ</c>, <c>&lt;hex&gt; Tj</c>.
    /// Line operators (BT, Td, TD, T*, ET) become newlines.
    /// </summary>
    private static void AppendTextOps(string content, StringBuilder sb)
    {
        var i = 0;
        var lastWasNewline = true;
        var n = content.Length;

        void AppendNewline()
        {
            if (lastWasNewline) return;
            sb.Append('\n');
            lastWasNewline = true;
        }

        while (i < n)
        {
            var c = content[i];

            if (c == '(')
            {
                var (text, next) = ReadPdfString(content, i);
                i = next;
                // Peek what operator follows (skipping spaces/numbers/arrays handled below).
                var op = PeekOperator(content, i);
                if (op is "Tj" or "'" or "\"")
                {
                    sb.Append(text);
                    lastWasNewline = false;
                    if (op is "'" or "\"") AppendNewline();
                    i = SkipOperator(content, i, op);
                }
                // else: bare string (metadata) — skip it.
                continue;
            }

            if (c == '<' && i + 1 < n && content[i + 1] != '<')
            {
                var close = content.IndexOf('>', i + 1);
                if (close > i)
                {
                    var hex = content[(i + 1)..close].Replace(" ", "").Replace("\n", "").Replace("\r", "");
                    i = close + 1;
                    var op = PeekOperator(content, i);
                    if (op is "Tj" or "'" or "\"")
                    {
                        sb.Append(HexToText(hex));
                        lastWasNewline = false;
                        i = SkipOperator(content, i, op);
                    }
                    continue;
                }
            }

            if (c == '[')
            {
                // TJ array: strings + kerning numbers.
                var close = FindArrayClose(content, i);
                if (close > i)
                {
                    var inner = content[(i + 1)..close];
                    EmitTjArray(inner, sb, ref lastWasNewline);
                    i = close + 1;
                    var op = PeekOperator(content, i);
                    if (op == "TJ") i = SkipOperator(content, i, "TJ");
                    continue;
                }
            }

            // Operators that imply a line break when they follow text.
            if (StartsAt(content, i, "T*") || StartsAt(content, i, "Td") || StartsAt(content, i, "TD") ||
                StartsAt(content, i, "ET") || StartsAt(content, i, "BT"))
            {
                AppendNewline();
                i += StartsAt(content, i, "T*") ? 2 : 2;
                continue;
            }

            i++;
        }
    }

    private static void EmitTjArray(string inner, StringBuilder sb, ref bool lastWasNewline)
    {
        var i = 0;
        while (i < inner.Length)
        {
            var c = inner[i];
            if (c == '(')
            {
                var (text, next) = ReadPdfString(inner, i);
                sb.Append(text);
                lastWasNewline = false;
                i = next;
            }
            else if (c == '-')
            {
                var j = i + 1;
                while (j < inner.Length && (char.IsDigit(inner[j]) || inner[j] == '.')) j++;
                if (int.TryParse(inner[i..j], out var kern) && kern <= -80)
                {
                    sb.Append(' '); // large negative kerning ≈ word gap
                    lastWasNewline = false;
                }
                i = j;
            }
            else i++;
        }
    }

    private static (string Text, int Next) ReadPdfString(string s, int open)
    {
        var sb = new StringBuilder();
        var i = open + 1;
        var depth = 1;
        while (i < s.Length && depth > 0)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var e = s[i + 1];
                switch (e)
                {
                    case 'n': sb.Append('\n'); i += 2; continue;
                    case 'r': sb.Append('\r'); i += 2; continue;
                    case 't': sb.Append('\t'); i += 2; continue;
                    case 'b': i += 2; continue;
                    case 'f': i += 2; continue;
                    case '(' or ')' or '\\': sb.Append(e); i += 2; continue;
                    case '\n': i += 2; continue;
                    case '\r': i += 2; if (i < s.Length && s[i] == '\n') i++; continue;
                    default:
                        if (e is >= '0' and <= '7')
                        {
                            var oct = new char[] { e };
                            var count = 1;
                            while (count < 3 && i + 1 + count < s.Length && s[i + 1 + count] is >= '0' and <= '7')
                                oct[count++] = s[i + 1 + count];
                            var val = Convert.ToInt32(new string(oct), 8);
                            sb.Append((char)val);
                            i += 1 + count;
                            continue;
                        }
                        sb.Append(e);
                        i += 2;
                        continue;
                }
            }
            if (c == '(') { depth++; sb.Append(c); i++; continue; }
            if (c == ')')
            {
                depth--;
                if (depth == 0) { i++; break; }
                sb.Append(c);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), i);
    }

    private static string HexToText(string hex)
    {
        if (hex.Length % 2 == 1) hex += "0";
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return Encoding.Latin1.GetString(bytes);
    }

    private static int FindArrayClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
            else if (s[i] == '(')
            {
                // Skip nested string contents so parens inside don't confuse us.
                var (_, next) = ReadPdfString(s, i);
                i = next - 1;
            }
        }
        return -1;
    }

    private static string? PeekOperator(string s, int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t')) i++;
        foreach (var op in new[] { "Tj", "TJ" })
            if (i + 2 <= s.Length && s.AsSpan(i, 2).SequenceEqual(op)) return op;
        if (i < s.Length && (s[i] == '\'' || s[i] == '"')) return s[i].ToString();
        return null;
    }

    private static int SkipOperator(string s, int i, string op)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t')) i++;
        return Math.Min(s.Length, i + op.Length);
    }

    private static bool StartsAt(string s, int i, string token) =>
        i + token.Length <= s.Length && s.AsSpan(i, token.Length).SequenceEqual(token);
}
