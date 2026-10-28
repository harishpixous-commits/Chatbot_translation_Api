using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;

namespace Pixous.AI.Core.Services;

// ── DTOs (public API contracts — stable across projects) ──────────────────
public sealed record ChatRequestDto(
    string Message,
    List<ChatMessage>? History = null,
    string? ResponseLanguage = null,
    string? Provider = null,
    string? SessionId = null);

public sealed record ChatResponseDto(
    string Reply,
    string Provider,
    string? Model,
    string? ResponseLanguage,
    string? SessionId);

public sealed record TranslateRequestDto(
    string Text,
    string TargetLanguage,
    string? SourceLanguage = null,
    string? Provider = null);

public sealed record TranslateResponseDto(
    string TranslatedText,
    string TargetLanguage,
    string? DetectedSourceLanguage,
    string Provider);

public sealed record TranslateBatchRequestDto(
    List<string> Texts,
    string TargetLanguage,
    string? SourceLanguage = null,
    string? Provider = null);

public sealed record TranslateBatchResponseDto(
    List<string> TranslatedTexts,
    string TargetLanguage,
    string Provider);

public sealed record DetectRequestDto(string Text, string? Provider = null);
public sealed record DetectResponseDto(string Language, double Confidence, string Provider);

// ── Chat orchestrator ────────────────────────────────────────────────────
public interface IChatService
{
    Task<ChatResponseDto> ChatAsync(ChatRequestDto request, CancellationToken ct = default);
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequestDto request, CancellationToken ct = default);
}

/// <summary>
/// Thin orchestrator: validate → (optional) load session → chat →
/// (optional) translate reply → (optional) persist session.
/// All business rules live here so controllers stay thin.
/// Never logs message content or secrets — lengths and outcomes only.
/// </summary>
public sealed class ChatService : IChatService
{
    private readonly IChatProviderFactory _chats;
    private readonly ITranslationProviderFactory _translators;
    private readonly IConversationStore? _store;
    private readonly ILogger<ChatService> _log;
    private readonly AiSuiteOptions _options;

    public ChatService(
        IChatProviderFactory chats,
        ITranslationProviderFactory translators,
        AiSuiteOptions options,
        ILogger<ChatService> log,
        IConversationStore? store = null)
    {
        _chats = chats;
        _translators = translators;
        _options = options;
        _log = log;
        _store = store;
    }

    public async Task<ChatResponseDto> ChatAsync(ChatRequestDto request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (history, targetLang) = await PrepareAsync(request, ct).ConfigureAwait(false);
        var provider = _chats.Get(request.Provider);
        try
        {
            var reply = await provider.ChatAsync(history,
                new ChatRequestOptions(SystemPrompt: _options.ChatDefaults.SystemPrompt, ResponseLanguage: targetLang), ct)
                .ConfigureAwait(false);
            var finalText = await MaybeTranslateAsync(reply.Content, targetLang, ct).ConfigureAwait(false);
            await PersistAsync(request.SessionId, history, finalText, ct).ConfigureAwait(false);
            _log.LogInformation("Chat ok. Provider={Provider} Lang={Lang} History={HistoryCount} ElapsedMs={Elapsed}",
                reply.Provider, targetLang ?? "-", history.Count, sw.ElapsedMilliseconds);
            return new ChatResponseDto(finalText, reply.Provider, reply.Model, targetLang, request.SessionId);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _log.LogWarning(ex, "Chat failed. Provider={Provider} ElapsedMs={Elapsed}",
                provider.ProviderName, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequestDto request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Live translation of a stream is out of scope: stream in the model's
        // language, then translate the final text via POST translate if needed.
        var (history, targetLang) = await PrepareAsync(request, ct).ConfigureAwait(false);
        var provider = _chats.Get(request.Provider);
        await foreach (var chunk in provider.StreamChatAsync(history,
            new ChatRequestOptions(SystemPrompt: _options.ChatDefaults.SystemPrompt, ResponseLanguage: targetLang), ct)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async Task<(IReadOnlyList<ChatMessage> History, string? TargetLang)> PrepareAsync(
        ChatRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Message must not be empty.", nameof(request));
        if (request.Message.Length > _options.ChatDefaults.MaxMessageChars)
            throw new ArgumentException($"Message exceeds {_options.ChatDefaults.MaxMessageChars} characters.");

        string? targetLang = request.ResponseLanguage;
        if (targetLang is not null && !_options.Languages.IsSupported(targetLang))
            throw new ArgumentException(
                $"Language '{targetLang}' is not in the configured allow-list: {string.Join(", ", _options.Languages.Supported)}.",
                nameof(request));

        var history = new List<ChatMessage>();
        if (request.SessionId is not null && _store is not null)
            history.AddRange(await _store.GetAsync(request.SessionId, ct).ConfigureAwait(false));
        if (request.History is not null)
            history.AddRange(request.History);
        history.Add(new ChatMessage(ChatRole.User, request.Message.Trim()));

        // Bound what we send upstream (cost + abuse control).
        var trimmed = history.TakeLast(_options.ChatDefaults.MaxHistoryMessages).ToList();
        return (trimmed, targetLang);
    }

    private async Task<string> MaybeTranslateAsync(string text, string? targetLang, CancellationToken ct)
    {
        if (targetLang is null) return text;
        var translator = _translators.Get(null);
        var result = await translator.TranslateAsync(text, targetLang, null, ct).ConfigureAwait(false);
        return result.TranslatedText;
    }

    private async Task PersistAsync(string? sessionId, IReadOnlyList<ChatMessage> history, string reply, CancellationToken ct)
    {
        if (sessionId is null || _store is null) return;
        // History already contains the new user message; append assistant reply.
        await _store.AppendAsync(sessionId,
            history.TakeLast(1).Concat(new[] { new ChatMessage(ChatRole.Assistant, reply) }), ct)
            .ConfigureAwait(false);
    }
}

// ── Translation orchestrator ─────────────────────────────────────────────
public interface ITranslationService
{
    Task<TranslateResponseDto> TranslateAsync(TranslateRequestDto request, CancellationToken ct = default);
    Task<TranslateBatchResponseDto> TranslateBatchAsync(TranslateBatchRequestDto request, CancellationToken ct = default);
    Task<DetectResponseDto> DetectAsync(DetectRequestDto request, CancellationToken ct = default);
    Task<IReadOnlyList<SupportedLanguage>> GetLanguagesAsync(CancellationToken ct = default);
}

public sealed class TranslationService : ITranslationService
{
    private readonly ITranslationProviderFactory _factory;
    private readonly AiSuiteOptions _options;
    private readonly ILogger<TranslationService> _log;

    public TranslationService(ITranslationProviderFactory factory, AiSuiteOptions options, ILogger<TranslationService> log)
    {
        _factory = factory;
        _options = options;
        _log = log;
    }

    public async Task<TranslateResponseDto> TranslateAsync(TranslateRequestDto request, CancellationToken ct = default)
    {
        ValidateText(request.Text, request.TargetLanguage);
        var sw = Stopwatch.StartNew();
        var provider = _factory.Get(request.Provider);
        try
        {
            var r = await provider.TranslateAsync(request.Text, request.TargetLanguage, request.SourceLanguage, ct).ConfigureAwait(false);
            _log.LogInformation("Translate ok. Provider={Provider} To={Lang} Chars={Chars} ElapsedMs={Elapsed}",
                r.Provider, r.TargetLanguage, request.Text.Length, sw.ElapsedMilliseconds);
            return new TranslateResponseDto(r.TranslatedText, r.TargetLanguage, r.DetectedSourceLanguage, r.Provider);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _log.LogWarning(ex, "Translate failed. Provider={Provider}", provider.ProviderName);
            throw;
        }
    }

    public async Task<TranslateBatchResponseDto> TranslateBatchAsync(TranslateBatchRequestDto request, CancellationToken ct = default)
    {
        if (request.Texts.Count == 0 || request.Texts.Count > 100)
            throw new ArgumentException("Batch must contain 1-100 texts.", nameof(request));
        if (!_options.Languages.IsSupported(request.TargetLanguage))
            throw new ArgumentException($"Language '{request.TargetLanguage}' is not in the configured allow-list.", nameof(request));

        var provider = _factory.Get(request.Provider);
        var out_ = new List<string>(request.Texts.Count);
        foreach (var t in request.Texts)
        {
            if (string.IsNullOrWhiteSpace(t)) { out_.Add(string.Empty); continue; }
            var r = await provider.TranslateAsync(t, request.TargetLanguage, request.SourceLanguage, ct).ConfigureAwait(false);
            out_.Add(r.TranslatedText);
        }
        return new TranslateBatchResponseDto(out_, request.TargetLanguage, provider.ProviderName);
    }

    public async Task<DetectResponseDto> DetectAsync(DetectRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text must not be empty.", nameof(request));
        var provider = _factory.Get(request.Provider);
        var r = await provider.DetectAsync(request.Text, ct).ConfigureAwait(false);
        return new DetectResponseDto(r.Language, r.Confidence, provider.ProviderName);
    }

    public Task<IReadOnlyList<SupportedLanguage>> GetLanguagesAsync(CancellationToken ct = default) =>
        _factory.Get(null).GetSupportedLanguagesAsync(ct);

    private void ValidateText(string text, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must not be empty.", nameof(text));
        if (text.Length > _options.ChatDefaults.MaxMessageChars)
            throw new ArgumentException($"Text exceeds {_options.ChatDefaults.MaxMessageChars} characters.");
        if (!_options.Languages.IsSupported(targetLanguage))
            throw new ArgumentException(
                $"Language '{targetLanguage}' is not in the configured allow-list: {string.Join(", ", _options.Languages.Supported)}.",
                nameof(targetLanguage));
    }
}
