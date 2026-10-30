using System.Diagnostics;
using System.Net;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers.Resilience;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>Caller cancellation during retry backoff stops promptly, with no further upstream call.</summary>
public class ResilienceBackoffCancellationTests
{
    private sealed class AlwaysStatus(HttpStatusCode code) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new HttpResponseMessage(code)); }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task CancelDuringBackoff_StopsPromptly_NoSecondCall(HttpStatusCode code, bool retryAmbiguous)
    {
        var handler = new AlwaysStatus(code);
        var opts = new ResilienceOptions { TimeoutSeconds = 10, MaxRetries = 3, BaseDelayMs = 20000, RetryAmbiguousFailures = retryAmbiguous };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProviderResilience.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "http://x.invalid/"), new HttpClient(handler), opts, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.Equal(1, handler.Calls);
    }
}
