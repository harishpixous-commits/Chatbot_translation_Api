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

    private static OpenAiCompatibleChatProvider WithExtras(CaptureHandler h, Dictionary<string, double>? extras) =>
        new(new HttpClient(h),
            new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost", RequireApiKey = false, Temperature = 0.7,
                ExtraSamplingParameters = extras
            },
            new ResilienceOptions(), apiKey: null);

    private static readonly ChatMessage[] Hi = { new(ChatRole.User, "hi") };

    [Fact]
    public async Task Chat_DefaultPayload_HasNoExtraSamplingFields()
    {
        var handler = new CaptureHandler();
        await WithExtras(handler, null).ChatAsync(Hi);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        foreach (var k in new[] { "top_p", "top_k", "min_p" })
            Assert.False(doc.RootElement.TryGetProperty(k, out _), k);
        Assert.Equal(0.7, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(512, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Chat_ConfiguredExtraSampling_IsSentUnchangedAndCoreFieldsIntact()
    {
        var handler = new CaptureHandler();
        await WithExtras(handler, new() { ["top_p"] = 0.8, ["top_k"] = 20, ["min_p"] = 0, ["seed"] = 1234 }).ChatAsync(Hi);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var r = doc.RootElement;
        Assert.Equal(0.8, r.GetProperty("top_p").GetDouble());
        Assert.Equal(20, r.GetProperty("top_k").GetDouble());
        Assert.Equal(0, r.GetProperty("min_p").GetDouble());
        Assert.Equal(1234, r.GetProperty("seed").GetInt32());
        Assert.Equal(0.7, r.GetProperty("temperature").GetDouble());
        Assert.Equal(512, r.GetProperty("max_tokens").GetInt32());
        Assert.Equal("user", r.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("temperature", 9)]
    [InlineData("max_tokens", 1)]
    [InlineData("model", 1)]
    [InlineData("stream", 1)]
    [InlineData("messages", 1)]
    [InlineData("unknown_field", 1)]
    [InlineData("top_p", 1.5)]
    [InlineData("top_p", -0.1)]
    [InlineData("top_p", double.NaN)]
    [InlineData("top_p", double.PositiveInfinity)]
    [InlineData("top_k", 20.5)]
    [InlineData("top_k", -1)]
    [InlineData("top_k", 100000)]
    [InlineData("min_p", 2)]
    [InlineData("seed", -1)]
    [InlineData("seed", 1.5)]
    [InlineData("seed", 3000000000)]
    public async Task Chat_InvalidExtraSampling_FailsClosed_NoHttpSent(string key, double value)
    {
        var handler = new CaptureHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithExtras(handler, new() { [key] = value }).ChatAsync(Hi));
        Assert.Null(handler.LastBody);
    }
}
