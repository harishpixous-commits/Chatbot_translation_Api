using System.Text;

namespace Pixous.AI.Providers;

/// <summary>
/// Incremental filter removing &lt;think&gt;...&lt;/think&gt; reasoning blocks
/// some local models (e.g. Qwen3) embed in chat content. Stateful across
/// streaming chunks: a tag split across SSE deltas is still recognized.
/// Text outside think blocks is preserved, including text that merely starts
/// like a tag. An unterminated think block at end-of-stream is dropped (it is
/// reasoning, not an answer); a dangling partial tag in normal state is
/// emitted as legitimate text on <see cref="Flush"/>.
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private bool _inThink;
    private string _pending = string.Empty;

    /// <summary>Feed one chunk; returns the text safe to emit now.</summary>
    public string Push(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return string.Empty;
        var work = _pending + delta;
        _pending = string.Empty;
        var sb = new StringBuilder(work.Length);
        var i = 0;
        while (i < work.Length)
        {
            var tag = _inThink ? Close : Open;
            var lt = work.IndexOf('<', i);
            if (lt < 0)
            {
                if (!_inThink) sb.Append(work, i, work.Length - i);
                break;
            }
            if (!_inThink) sb.Append(work, i, lt - i);
            var rest = work.Substring(lt);
            if (rest.StartsWith(tag, StringComparison.Ordinal))
            {
                _inThink = !_inThink;
                i = lt + tag.Length;
                continue;
            }
            if (rest.Length < tag.Length && tag.StartsWith(rest, StringComparison.Ordinal))
            {
                _pending = rest; // possible tag split across chunks
                break;
            }
            if (!_inThink) sb.Append('<'); // a real '<', keep it
            i = lt + 1;
        }
        return sb.ToString();
    }

    /// <summary>End of stream: emit any dangling partial tag outside a think block.</summary>
    public string Flush()
    {
        var rest = _pending;
        _pending = string.Empty;
        return _inThink ? string.Empty : rest;
    }

    /// <summary>One-shot strip for assembled (non-streaming) content.</summary>
    public static string Strip(string text)
    {
        var f = new ThinkTagFilter();
        return f.Push(text) + f.Flush();
    }
}
