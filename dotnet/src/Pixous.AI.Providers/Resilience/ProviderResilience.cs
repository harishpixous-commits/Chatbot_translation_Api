using Pixous.AI.Core.Configuration;

namespace Pixous.AI.Providers.Resilience;

/// <summary>
/// Shared retry/timeout policy for all HTTP providers (no Polly dependency).
/// Default-deny for billable calls: only HTTP 429 (a definitive rejection —
/// no completion was produced) is retried. Timeouts, 5xx and network errors
/// are AMBIGUOUS — the provider may have processed and billed the request —
/// so they are retried only when <see cref="ResilienceOptions.RetryAmbiguousFailures"/>
/// is explicitly enabled. 4xx/auth errors always fail fast.
/// </summary>
public static class ProviderResilience
{
    public static async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpClient http,
        ResilienceOptions options,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            using var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Provider-side timeout (caller did NOT cancel): AMBIGUOUS — the
                // request may have been processed and billed. Never retry unless
                // the operator explicitly opted in.
                if (!options.RetryAmbiguousFailures || attempt >= options.MaxRetries)
                    throw new HttpRequestException(
                        $"Provider timed out after {options.TimeoutSeconds}s. The request may still have been processed — not retried automatically.");
                await BackoffAsync(attempt, options, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }
            catch (HttpRequestException)
            {
                // Network failure: ambiguous for the same reason.
                if (!options.RetryAmbiguousFailures || attempt >= options.MaxRetries) throw;
                await BackoffAsync(attempt, options, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            if (!IsRetryable((int)response.StatusCode, options) || attempt >= options.MaxRetries)
                return response;

            response.Dispose();
            await BackoffAsync(attempt, options, ct).ConfigureAwait(false);
            attempt++;
        }
    }

    /// <summary>
    /// Streaming variant: the SAME default-deny retry discipline applies, but
    /// only until response HEADERS arrive (ResponseHeadersRead). After that the
    /// body is an open SSE stream owned by the caller: mid-stream failures are
    /// never retried here (the prompt may already be processed and billed, and
    /// partial output may already be shown). The linked timeout covers header
    /// receipt; the caller's cancellation covers the stream itself, so the
    /// HttpClient must not impose a short total timeout.
    /// </summary>
    public static async Task<HttpResponseMessage> SendStreamingAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpClient http,
        ResilienceOptions options,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            using var request = requestFactory();
            HttpResponseMessage response;
            // The headers-phase timeout token stays LINKED to the response body
            // stream by HttpClient, so its source must live as long as the
            // response: ownership transfers to LinkedLifetimeContent on success.
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timeout.Dispose();
                if (!options.RetryAmbiguousFailures || attempt >= options.MaxRetries)
                    throw new HttpRequestException(
                        $"Provider timed out after {options.TimeoutSeconds}s before streaming started. The request may still have been processed — not retried automatically.");
                await BackoffAsync(attempt, options, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }
            catch (HttpRequestException)
            {
                timeout.Dispose();
                if (!options.RetryAmbiguousFailures || attempt >= options.MaxRetries) throw;
                await BackoffAsync(attempt, options, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            // Headers received: the timeout covered connection + headers only.
            // A long-lived SSE body must NOT be cut off mid-stream.
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);

            if (!IsRetryable((int)response.StatusCode, options) || attempt >= options.MaxRetries)
            {
                response.Content = new LinkedLifetimeContent(response.Content, timeout);
                return response;
            }

            response.Dispose();
            timeout.Dispose();
            await BackoffAsync(attempt, options, ct).ConfigureAwait(false);
            attempt++;
        }
    }

    /// <summary>
    /// Wraps a streaming response body so the headers-phase timeout source
    /// (whose token HttpClient keeps linked to the body stream) is disposed
    /// together with the response, not when the send helper returns.
    /// </summary>
    private sealed class LinkedLifetimeContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _timeout;

        public LinkedLifetimeContent(HttpContent inner, CancellationTokenSource timeout)
        {
            _inner = inner;
            _timeout = timeout;
            foreach (var h in inner.Headers)
                Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            _inner.ReadAsStreamAsync(cancellationToken);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            _inner.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _timeout.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Reads a non-success streaming error body (bounded) and throws.</summary>
    public static async Task<LlmCallException> ToCallExceptionAsync(
        HttpResponseMessage httpResponse, string vendor, CancellationToken ct)
    {
        var body = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var retryAfter = httpResponse.Headers.RetryAfter?.Delta?.TotalSeconds is { } d
            ? ((int)d).ToString()
            : httpResponse.Headers.RetryAfter?.Date is { } dt
                ? ((int)(dt - DateTimeOffset.UtcNow).TotalSeconds).ToString()
                : null;
        var trimmed = body.Length <= 300 ? body : body[..300] + "…";
        return new LlmCallException(
            (int)httpResponse.StatusCode,
            $"{vendor} returned {(int)httpResponse.StatusCode}. {trimmed}",
            retryAfter);
    }

    private static bool IsRetryable(int status, ResilienceOptions options) =>
        // 429: the provider rejected the call — no completion, safe to retry.
        status == 429 ||
        // 408/5xx: ambiguous (may have been processed) — only when opted in.
        (options.RetryAmbiguousFailures && (status == 408 || status >= 500));

    private static Task BackoffAsync(int attempt, ResilienceOptions options, CancellationToken ct)
    {
        var ms = options.BaseDelayMs * (1 << attempt) + Random.Shared.Next(0, 250);
        return Task.Delay(ms, ct);
    }
}
