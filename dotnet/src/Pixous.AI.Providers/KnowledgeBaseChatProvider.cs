using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;

namespace Pixous.AI.Providers;

/// <summary>
/// Config-driven smart-answer provider ("KnowledgeBase"): FAQ entries from
/// <c>AiSuite:KnowledgeBase</c> (match phrases → answers) plus any documents
/// uploaded through the ⚙ Settings panel (PDF/DOCX/MD — chunk + keyword
/// retrieval via <see cref="SettingsStore"/>). Best score wins; streams
/// word-by-word for a typewriter UX without an LLM.
/// </summary>
public sealed class KnowledgeBaseChatProvider : IChatProvider
{
    public string ProviderName => "KnowledgeBase";

    private readonly List<KnowledgeEntry> _entries;
    private readonly string _fallbackAnswer;
    private readonly SettingsStore? _store;

    public KnowledgeBaseChatProvider(KnowledgeBaseOptions options, SettingsStore? store = null)
    {
        _entries = options.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Answer) && e.Match.Count > 0)
            .ToList();
        _fallbackAnswer = string.IsNullOrWhiteSpace(options.FallbackAnswer)
            ? "I don't have an answer for that yet."
            : options.FallbackAnswer;
        _store = store;
    }

    public Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(Answer(history), ProviderName, "knowledge-base-v1"));

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var answer = Answer(history);
        foreach (var word in answer.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return new ChatChunk(word + " ");
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        yield return new ChatChunk(string.Empty, IsFinal: true);
    }

    internal string Answer(IReadOnlyList<ChatMessage> history)
    {
        var last = history.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;

        // 1) FAQ entries — explicit phrase matching (most specific wins).
        var best = _entries
            .Select(e => (Entry: e, Score: Score(e, last)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        // 2) Uploaded documents — keyword/token retrieval, only beats FAQ when clearly better.
        var docHit = _store?.Search(last, topN: 1, minScore: 3).FirstOrDefault();

        if (docHit is not null && (best.Entry is null || docHit.Score > best.Score * 1.5))
            return $"**From {DisplayName(docHit.Source)}:**\n\n{docHit.Text}";

        return best.Entry?.Answer ?? _fallbackAnswer;
    }

    private static string DisplayName(string source) =>
        source.StartsWith("upload:", StringComparison.OrdinalIgnoreCase)
            ? source["upload:".Length..]
            : "knowledge base";

    private static int Score(KnowledgeEntry entry, string input)
    {
        var score = 0;
        foreach (var phrase in entry.Match)
        {
            if (string.IsNullOrWhiteSpace(phrase)) continue;
            if (input.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                score += 1 + phrase.Trim().Length / 10; // longer phrases = more specific
        }
        return score;
    }
}
