namespace Pixous.AI.Abstractions;

/// <summary>Role of a single chat message. String-serialized for every provider wire format.</summary>
public enum ChatRole
{
    System,
    User,
    Assistant
}

/// <summary>One message in a conversation.</summary>
public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>Per-call knobs. All optional; providers fall back to configuration.</summary>
public sealed record ChatRequestOptions(
    string? SystemPrompt = null,
    string? ModelOverride = null,
    int? MaxTokens = null,
    double? Temperature = null,
    string? ResponseLanguage = null);

/// <summary>Non-streaming chat answer.</summary>
public sealed record ChatResponse(string Content, string Provider, string? Model = null);

/// <summary>One streaming chunk (SSE / SignalR friendly).</summary>
public sealed record ChatChunk(string Delta, bool IsFinal = false);

/// <summary>
/// Conversational-AI backend seam: Azure OpenAI, OpenAI, Anthropic, Google,
/// local models, knowledge bases, mocks. New provider = implement + register.
/// </summary>
public interface IChatProvider
{
    /// <summary>Name used in configuration/selection (e.g. "OpenAiCompatible", "KnowledgeBase", "Echo").</summary>
    string ProviderName { get; }

    /// <summary>Full reply in one shot (default folds <see cref="StreamChatAsync"/>).</summary>
    Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return FoldStreamAsync(history, options, cancellationToken);
    }

    /// <summary>Token-by-token reply. Must always end with <see cref="ChatChunk.IsFinal"/> = true.</summary>
    IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options = null,
        CancellationToken cancellationToken = default);

    async Task<ChatResponse> FoldStreamAsync(
        IReadOnlyList<ChatMessage> history,
        ChatRequestOptions? options,
        CancellationToken cancellationToken)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in StreamChatAsync(history, options, cancellationToken).ConfigureAwait(false))
            sb.Append(chunk.Delta);
        return new ChatResponse(sb.ToString(), ProviderName);
    }
}

/// <summary>Resolves the effective chat provider. Precedence: per-request → runtime → config.</summary>
public interface IChatProviderFactory
{
    IChatProvider Get(string? providerName = null);
    IEnumerable<string> RegisteredProviders { get; }
}
