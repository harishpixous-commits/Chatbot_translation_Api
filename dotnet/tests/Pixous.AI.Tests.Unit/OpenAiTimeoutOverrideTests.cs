using System.Net;
using System.Text;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>
/// Local CPU inference (llama.cpp/Ollama) legitimately exceeds the 60s global
/// default on low-core machines — a short Qwen3-4B Q4_K_M reply measured 85.5s
/// on a 2-vCPU Windows CI runner. OpenAiCompatible:TimeoutSeconds scopes a
/// longer budget to this provider ONLY; the global Resilience default (a
/// billing guard for hosted vendors) is untouched.
/// </summary>
public class OpenAiTimeoutOverrideTests
{
    private sealed class DelayHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // Honors ct: a provider-side timeout cancels this wait, surfacing as
            // OperationCanceledException like a real slow endpoint.
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"test-model","choices":[{"message":{"content":"slow-but-ok"}}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static OpenAiCompatibleChatProvider Provider(
        HttpMessageHandler handler, int globalTimeout, int? overrideTimeout) =>
        new(
            new HttpClient(handler),
            new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost",
                RequireApiKey = false,
                TimeoutSeconds = overrideTimeout
            },
            new ResilienceOptions { TimeoutSeconds = globalTimeout, MaxRetries = 0 },
            apiKey: null);

    [Fact]
    public async Task Override_AllowsSlowLocalModel_BeyondGlobalDefault()
    {
        // Global 1s would cut this 2s 'model' off; provider override 10s lets it answer.
        var provider = Provider(new DelayHandler(TimeSpan.FromSeconds(2)), globalTimeout: 1, overrideTimeout: 10);

        var reply = await provider.ChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        Assert.Equal("slow-but-ok", reply.Content);
    }

    [Fact]
    public async Task NoOverride_GlobalDefaultStillCutsSlowProvider()
    {
        // Same 2s 'model', global 1s, no override: must time out (billing-guard
        // semantics preserved) and must NOT be retried (MaxRetries 0 + ambiguous).
        var provider = Provider(new DelayHandler(TimeSpan.FromSeconds(2)), globalTimeout: 1, overrideTimeout: null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.ChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        Assert.Contains("timed out after 1s", ex.Message);
        Assert.Contains("may still have been processed", ex.Message);
    }

    [Fact]
    public async Task Override_StillTimesOut_WhenProviderExceedsIt()
    {
        // Override is not "infinite patience": a 6s 'model' with a 3s override fails closed.
        var provider = Provider(new DelayHandler(TimeSpan.FromSeconds(6)), globalTimeout: 60, overrideTimeout: 3);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.ChatAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        Assert.Contains("timed out after 3s", ex.Message);
    }
}
