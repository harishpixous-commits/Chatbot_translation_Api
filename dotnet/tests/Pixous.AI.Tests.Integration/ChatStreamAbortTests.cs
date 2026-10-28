using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pixous.AI.Abstractions;
using Xunit;

namespace Pixous.AI.Tests.Integration;

/// <summary>
/// Mid-SSE provider failure at the ENDPOINT layer: headers and at least one
/// delta are already on the wire, so a 502 status is impossible - the failure
/// must surface as an explicit SSE error frame (UPSTREAM_ABORTED), never a
/// silent blank stream or a clean [DONE] after partial output.
/// </summary>
public class ChatStreamAbortTests : IClassFixture<ChatStreamAbortTests.Factory>
{
    private readonly HttpClient _client;

    /// <summary>Provider that emits one real delta, then dies mid-stream with
    /// the exact exception the malformed-SSE-frame guard throws.</summary>
    public sealed class ThrowingChatProvider : IChatProvider
    {
        public string ProviderName => "Throwing";

        public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
            IReadOnlyList<ChatMessage> history,
            ChatRequestOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatChunk("partial answer text");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            throw new System.IO.InvalidDataException(
                "Chat backend sent an error frame mid-stream: bad key sk-liveSENTINELkey9999 " +
                "see http://169.254.10.9:9000/internal/admin authorization: Bearer sentinel-bearer-token-12345 " +
                "acct-0000 private freeform sentence quxberry");
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AiSuite:TranslationProvider", "Echo");
            builder.UseSetting("AiSuite:ChatProvider", "Throwing");
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatProvider>(new ThrowingChatProvider()));
        }
    }

    public ChatStreamAbortTests(Factory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ChatStream_MidStreamProviderFailure_EmitsErrorFrame_NotBlankDone()
    {
        var res = await _client.PostAsJsonAsync("/api/ai/chat/stream", new { message = "hi" });
        res.EnsureSuccessStatusCode(); // headers went out before the failure
        var body = await res.Content.ReadAsStringAsync();

        Assert.Contains("partial answer text", body);          // the delta before the failure
        Assert.Contains("{\"error\"", body);                    // explicit error frame
        Assert.Contains("\"code\":\"UPSTREAM_ABORTED\"", body); // typed, not generic
        Assert.Contains("The response stream was interrupted", body); // FIXED allowlisted client text
        Assert.DoesNotContain("data: [DONE]", body);            // never a clean terminator after failure
        // NO provider-authored text reaches the client frame: not the message
        // shape, not credential sentinels, not the URL, and not a plain
        // arbitrary private sentence (non-credential-shaped, unpatterned).
        Assert.DoesNotContain("error frame mid-stream", body);
        Assert.DoesNotContain("sk-liveSENTINELkey9999", body);
        Assert.DoesNotContain("sentinel-bearer-token-12345", body);
        Assert.DoesNotContain("169.254.10.9", body);
        Assert.DoesNotContain("quxberry", body);
    }
}
