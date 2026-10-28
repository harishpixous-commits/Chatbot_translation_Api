namespace Pixous.AI.Abstractions;

/// <summary>
/// Optional conversation persistence seam. The core library never touches a
/// database directly — the host plugs in whatever it already uses:
/// in-memory (default), SQL, MongoDB, Redis, or a custom implementation.
/// Not configured = stateless mode (caller passes <c>History</c> per request).
/// </summary>
public interface IConversationStore
{
    /// <summary>Stored messages for a session (oldest first). Empty when unknown.</summary>
    Task<IReadOnlyList<ChatMessage>> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Appends messages to a session. Implementations should bound growth.</summary>
    Task AppendAsync(string sessionId, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Drops a session.</summary>
    Task ClearAsync(string sessionId, CancellationToken cancellationToken = default);
}
