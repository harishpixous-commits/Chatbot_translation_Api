using System.Net;
using System.Text;
using System.Text.Json;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Providers;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>
/// The configured system prompt (AiSuite:ChatDefaults:SystemPrompt) must reach
/// the upstream model as the first "system" message — without it the chat has
/// no agent identity, rules or guardrails.
/// </summary>
public class OpenAiSystemPromptTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"test-model","choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static OpenAiCompatibleChatProvider Provider(CaptureHandler handler) =>
        new(
            new HttpClient(handler),
            new OpenAiCompatibleOptions { Endpoint = "http://localhost", RequireApiKey = false },
            new ResilienceOptions(),
            apiKey: null);

    [Fact]
    public async Task Chat_SendsSystemPrompt_AsFirstMessage()
    {
        var handler = new CaptureHandler();
        var provider = Provider(handler);

        await provider.ChatAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatRequestOptions(SystemPrompt: "RULE ONE: be brief."));

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.True(messages.GetArrayLength() >= 2, "history + system message expected");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("RULE ONE: be brief.", messages[0].GetProperty("content").GetString());
        var last = messages[messages.GetArrayLength() - 1];
        Assert.Equal("user", last.GetProperty("role").GetString());
        Assert.Equal("hi", last.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Chat_WithoutSystemPrompt_AddsNoSystemMessage()
    {
        var handler = new CaptureHandler();
        var provider = Provider(handler);

        await provider.ChatAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatRequestOptions());

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Chat_HistoryAlreadyHasSystem_DoesNotDuplicate()
    {
        var handler = new CaptureHandler();
        var provider = Provider(handler);

        await provider.ChatAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, "existing system rule"),
                new ChatMessage(ChatRole.User, "hi"),
            },
            new ChatRequestOptions(SystemPrompt: "configured prompt"));

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("existing system rule", messages[0].GetProperty("content").GetString());
    }
}
