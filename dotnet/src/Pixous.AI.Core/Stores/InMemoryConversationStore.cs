using System.Collections.Concurrent;
using Pixous.AI.Abstractions;

namespace Pixous.AI.Core.Stores;

/// <summary>
/// Default <see cref="IConversationStore"/>: process-local, thread-safe,
/// sessions bounded to the newest 100 messages. Swap for SQL / MongoDB /
/// Redis in the host by registering your own <see cref="IConversationStore"/>.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private const int MaxPerSession = 100;
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _sessions = new();

    public Task<IReadOnlyList<ChatMessage>> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Task.FromResult<IReadOnlyList<ChatMessage>>(
            _sessions.TryGetValue(sessionId, out var list) ? list.ToList() : Array.Empty<ChatMessage>());
    }

    public Task AppendAsync(string sessionId, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var list = _sessions.GetOrAdd(sessionId, _ => new List<ChatMessage>());
        lock (list)
        {
            list.AddRange(messages);
            if (list.Count > MaxPerSession)
                list.RemoveRange(0, list.Count - MaxPerSession);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}
