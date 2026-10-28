namespace Pixous.AI.AspNetCore.Hosting;

/// <summary>Standard error shape. Never carries stack traces, keys or provider internals.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>
/// Standard envelope for every JSON endpoint (spec §14):
/// <c>{ success, data, error: { code, message }, traceId }</c>.
/// Streaming (<c>/chat/stream</c>) is the only exception (SSE frames).
/// </summary>
public sealed record ApiResponse<T>(bool Success, T? Data, ApiError? Error, string TraceId);

/// <summary>Null (or "default") = fall back to configuration.</summary>
public sealed record SelectionRequestDto(string? ChatProvider, string? TranslationProvider);
