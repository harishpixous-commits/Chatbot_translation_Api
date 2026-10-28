using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

public class ThinkTagFilterTests
{
    [Fact]
    public void OneShot_StripsThinkBlock_KeepsAnswer()
    {
        Assert.Equal("Tokyo", ThinkTagFilter.Strip("<think>\n\n</think>\n\nTokyo").Trim());
    }

    [Fact]
    public void OneShot_NoTags_PreservesByteForByte()
    {
        const string s = "a < b and <thinking> is not a tag, nor is <thinx>";
        Assert.Equal(s, ThinkTagFilter.Strip(s));
    }

    [Fact]
    public void OneShot_MultipleBlocks_AllStripped()
    {
        Assert.Equal("AB", ThinkTagFilter.Strip("<think>r1</think>A<think>r2</think>B"));
    }

    [Fact]
    public void OneShot_UnterminatedThink_Dropped()
    {
        Assert.Equal("answer", ThinkTagFilter.Strip("answer<think>reasoning never closed"));
    }

    [Fact]
    public void OneShot_DanglingPartialTag_EmittedAsLegitText()
    {
        Assert.Equal("value <th", ThinkTagFilter.Strip("value <th"));
    }

    [Theory]
    [InlineData(new[] { "<th", "ink>secret</th", "ink>visible" }, "visible")]
    [InlineData(new[] { "<", "t", "h", "i", "n", "k", ">x</think>ok" }, "ok")]
    [InlineData(new[] { "a<thi", "nk>b</think>c", "" }, "ac")]
    [InlineData(new[] { "<think>x</thi", "nk>tail" }, "tail")]
    [InlineData(new[] { "pre <th", "inx not a tag" }, "pre <thinx not a tag")]
    [InlineData(new[] { "<think>a</think>", "<think>b</think>", "done" }, "done")]
    public void Streaming_TagsSplitAcrossChunks_StillStripped(string[] chunks, string expected)
    {
        var f = new ThinkTagFilter();
        var actual = string.Concat(chunks.Select(f.Push)) + f.Flush();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Adversarial_CaseVariants_PassThrough_AsLegitText()
    {
        // Only exact lowercase tags are reasoning markers per the Qwen3 convention.
        // "<Think>" is not stripped - safe direction: never eat legitimate text.
        const string s = "I use <Think> and <THINK> as literal placeholders";
        Assert.Equal(s, ThinkTagFilter.Strip(s));
    }

    [Fact]
    public void Adversarial_LiteralDiscussion_OfThinkTags_InUserText()
    {
        // A user discussing the tags: the OUTER pair is still a think block by
        // convention - stripping is an output convention, not a security boundary.
        const string s = "how do I write <think> in a prompt?</think>";
        // by convention everything between the literal pair is a think block
        Assert.Equal("how do I write ", ThinkTagFilter.Strip(s));
    }

    [Fact]
    public void Adversarial_NestedThink_InnerTagsConsumed_OuterCloseEnds()
    {
        // No depth counting: first </think> ends the block; the trailing close tag
        // is literal text. Documented behavior, not a confused-deputy defense.
        Assert.Equal("x</think>", ThinkTagFilter.Strip("<think>a<think>b</think>x</think>"));
    }

    [Fact]
    public void Adversarial_MalformedTags_PassThrough()
    {
        // spaced forms are not tags; a trailing unterminated "<think" is a
        // dangling partial -> emitted as legitimate text on Flush
        const string s = "< think>not a tag</think > and <think";
        Assert.Equal(s, ThinkTagFilter.Strip(s));
    }

    [Fact]
    public void Adversarial_OnlyThinkContent_YieldsEmpty()
    {
        Assert.Equal(string.Empty, ThinkTagFilter.Strip("<think>everything was reasoning</think>"));
    }

    [Fact]
    public void Streaming_EmptyThinkBlockLikeQwen3_NoResidue()
    {
        var f = new ThinkTagFilter();
        var actual = string.Concat(new[] { "<think>\n\n", "</think>\n\n", "Tokyo" }.Select(f.Push)) + f.Flush();
        Assert.Equal("Tokyo", actual.Trim());
    }
}
