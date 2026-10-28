using Pixous.AI.Abstractions;

namespace Pixous.AI.Providers;

/// <summary>Keyless chat provider for development, demos and automated tests.</summary>
public sealed class EchoChatProvider : IChatProvider
{
    public string ProviderName => "Echo";

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var last = history.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? "(no input)";
        var reply = $"[Echo] You said: {last}";
        var mid = reply.Length / 2;
        yield return new ChatChunk(reply[..mid]);
        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        yield return new ChatChunk(reply[mid..]);
        yield return new ChatChunk(string.Empty, IsFinal: true);
    }
}
