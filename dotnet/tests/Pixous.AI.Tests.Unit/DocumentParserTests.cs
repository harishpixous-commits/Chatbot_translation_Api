using System.IO.Compression;
using System.Text;
using Pixous.AI.Core.Documents;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>Knowledge uploads: parsers (MD/TXT/DOCX/PDF) + chunking + keywords.</summary>
public class DocumentParserTests
{
    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    // ── MD / TXT ─────────────────────────────────────────────────────────
    [Fact]
    public void Markdown_ReadsAsIs()
    {
        var doc = DocumentParser.Parse("notes.md", Bytes("# Title\n\nSome **bold** content.\n- item"));
        Assert.Contains("# Title", doc.Text);
        Assert.Contains("Some **bold** content.", doc.Text);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Txt_ReadsAsIs()
    {
        var doc = DocumentParser.Parse("a.txt", Bytes("plain text body"));
        Assert.Equal("plain text body", doc.Text);
    }

    [Fact]
    public void UnsupportedType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => DocumentParser.Parse("deck.pptx", Bytes("x")));
        Assert.Contains(".pdf", ex.Message);
        Assert.Contains(".docx", ex.Message);
    }

    [Fact]
    public void EmptyFile_Throws()
    {
        Assert.Throws<ArgumentException>(() => DocumentParser.Parse("empty.md", Bytes("   ")));
    }

    // ── DOCX ─────────────────────────────────────────────────────────────
    private static MemoryStream BuildDocx(params string[] paragraphs)
    {
        var xml = new StringBuilder();
        xml.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        foreach (var p in paragraphs)
        {
            xml.Append("<w:p><w:r><w:t>").Append(System.Net.WebUtility.HtmlEncode(p)).Append("</w:t></w:r></w:p>");
        }
        xml.Append("</w:body></w:document>");

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(xml.ToString());
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Docx_ExtractsParagraphs()
    {
        var doc = DocumentParser.Parse("report.docx", BuildDocx("First paragraph.", "Second paragraph with & entity."));
        Assert.Contains("First paragraph.", doc.Text);
        Assert.Contains("Second paragraph with & entity.", doc.Text);
        // Paragraphs become separate lines.
        Assert.Contains("First paragraph.\nSecond", doc.Text);
    }

    [Fact]
    public void Docx_CorruptArchive_Throws()
    {
        Assert.Throws<ArgumentException>(() => DocumentParser.Parse("bad.docx", Bytes("this is not a zip")));
    }

    // ── PDF ──────────────────────────────────────────────────────────────
    private static MemoryStream BuildPdf(string contentStream, bool deflate = false)
    {
        var payload = deflate ? Deflate(contentStream) : Encoding.Latin1.GetBytes(contentStream);
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.4\n");
        W("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        W("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        W("3 0 obj << /Type /Page /Parent 2 0 R /Contents 4 0 R >> endobj\n");
        W($"4 0 obj << /Length {payload.Length}" + (deflate ? " /Filter /FlateDecode" : "") + " >>\nstream\n");
        ms.Write(payload);
        W("\nendstream\nendobj\n");
        W("trailer << /Root 1 0 R >>\n%%EOF");
        ms.Position = 0;
        return ms;
    }

    private static byte[] Deflate(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            z.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private const string SampleOps =
        "BT /F1 12 Tf 72 720 Td (Hello Quantum PDF) Tj T* (Second line here) Tj ET";

    [Fact]
    public void Pdf_Uncompressed_ExtractsText()
    {
        var doc = DocumentParser.Parse("sample.pdf", BuildPdf(SampleOps));
        Assert.Contains("Hello Quantum PDF", doc.Text);
        Assert.Contains("Second line here", doc.Text);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Pdf_FlateDecoded_ExtractsText()
    {
        var doc = DocumentParser.Parse("sample.pdf", BuildPdf(SampleOps, deflate: true));
        Assert.Contains("Hello Quantum PDF", doc.Text);
        Assert.Contains("Second line here", doc.Text);
    }

    [Fact]
    public void Pdf_TjArray_WithKerning_ExtractsWords()
    {
        var ops = "BT [(Refund) -120 (policy)] TJ ET";
        var doc = DocumentParser.Parse("arr.pdf", BuildPdf(ops));
        Assert.Contains("Refund policy", doc.Text);
    }

    [Fact]
    public void Pdf_EscapesAndHex_Extracted()
    {
        var ops = "BT (Line\\(one\\) ok) Tj <486578> Tj ET"; // "Hex"
        var doc = DocumentParser.Parse("esc.pdf", BuildPdf(ops));
        Assert.Contains("Line(one) ok", doc.Text);
        Assert.Contains("Hex", doc.Text);
    }

    [Fact]
    public void Pdf_ImageOnly_ReturnsWarning_NoText()
    {
        var doc = DocumentParser.Parse("scan.pdf", BuildPdf("q 1 0 0 1 0 0 cm /Im0 Do Q"));
        Assert.Equal(string.Empty, doc.Text);
        var warn = Assert.Single(doc.Warnings);
        Assert.Contains("scan", warn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pdf_NotAPdf_Throws()
    {
        Assert.Throws<ArgumentException>(() => DocumentParser.Parse("x.pdf", Bytes("hello world")));
    }

    // ── chunking + keywords ──────────────────────────────────────────────
    [Fact]
    public void Chunk_SplitsLongText_KeepsParagraphs()
    {
        var one = string.Join("\n\n", Enumerable.Range(0, 40).Select(i =>
            $"Paragraph {i} covers distinct information about topic number {i} in detail."));
        var chunks = KnowledgeChunker.Chunk(one);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 1400, "chunk kept reasonably sized"));
        Assert.Contains(chunks, c => c.Contains("Paragraph 0"));
        Assert.Contains(chunks, c => c.Contains("Paragraph 39"));
    }

    [Fact]
    public void Chunk_ShortText_SingleChunk()
    {
        var chunks = KnowledgeChunker.Chunk("Just one short paragraph.");
        Assert.Single(chunks);
        Assert.Equal("Just one short paragraph.", chunks[0]);
    }

    [Fact]
    public void Keywords_DropsStopwords_PrioritizesFrequency()
    {
        var words = KnowledgeChunker.Keywords(
            "Refund refund refund policy applies within thirty days of purchase. The refund refund policy is strict.");
        Assert.Contains("refund", words);
        Assert.DoesNotContain("the", words);
        Assert.DoesNotContain("within", words);
        Assert.Equal("refund", words[0]); // most frequent
    }

    [Fact]
    public void Tokenize_ForSearch()
    {
        var tokens = KnowledgeChunker.Tokenize("What is the Quantum-Entanglement policy?");
        Assert.Contains("quantum", tokens);
        Assert.Contains("entanglement", tokens);
        Assert.Contains("policy", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("what", tokens);
    }
}
