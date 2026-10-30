using Microsoft.Extensions.Logging.Abstractions;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>
/// CHARACTERIZATION tests: they pin the CURRENT public-API behavior of ChatService
/// for caller-controlled fields. They document an open trust-boundary question
/// (System-role history, unbounded Temperature); they do not endorse it. If the
/// behavior is hardened, update these tests deliberately, never delete them silently.
/// </summary>
public class ChatServiceTrustBoundaryTests
{
    private sealed class Spy : IChatProvider
    {
        public IReadOnlyList<ChatMessage>? History;
        public ChatRequestOptions? Options;
        public string ProviderName => "Spy";
        public Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> history, ChatRequestOptions? options = null, CancellationToken ct = default)
        { History = history; Options = options; return Task.FromResult(new ChatResponse("ok", ProviderName, "m")); }
        public async IAsyncEnumerable<ChatChunk> StreamChatAsync(IReadOnlyList<ChatMessage> history, ChatRequestOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { History = history; Options = options; yield return new ChatChunk("ok"); await Task.CompletedTask; }
    }

    private static (ChatService Svc, Spy Spy) Make(bool allowClientSystem = false)
    {
        var spy = new Spy();
        var opts = TestOptions.Echo();
        opts.ChatDefaults.AllowClientSystemMessages = allowClientSystem;
        var svc = new ChatService(new StubChatFactory(spy), new StubTranslationFactory(new Pixous.AI.Providers.EchoTranslationProvider()),
            opts, NullLogger<ChatService>.Instance);
        return (svc, spy);
    }

    [Fact]
    public async Task UntrustedDefault_ClientSystemRoleHistory_IsDropped_UserAssistantKept()
    {
        var (svc, spy) = Make();
        await svc.ChatAsync(new ChatRequestDto("hi", History: new()
            { new(ChatRole.System, "caller rule"), new(ChatRole.User, "u0"), new(ChatRole.Assistant, "a0") }));
        Assert.DoesNotContain(spy.History!, m => m.Role == ChatRole.System);
        Assert.Equal(new[] { "u0", "a0", "hi" }, spy.History!.Select(m => m.Content).ToArray());
        Assert.Equal(TestOptions.Echo().ChatDefaults.SystemPrompt, spy.Options!.SystemPrompt);
    }

    [Fact]
    public async Task ExplicitTrustedMode_ClientSystemRoleHistory_ReachesProvider()
    {
        var (svc, spy) = Make(allowClientSystem: true);
        await svc.ChatAsync(new ChatRequestDto("hi", History: new() { new(ChatRole.System, "caller rule") }));
        Assert.Contains(spy.History!, m => m.Role == ChatRole.System && m.Content == "caller rule");
    }

    [Fact]
    public async Task UntrustedDefault_StreamPath_AlsoDropsClientSystemRole()
    {
        var (svc, spy) = Make();
        await foreach (var _ in svc.StreamChatAsync(new ChatRequestDto("hi", History: new() { new(ChatRole.System, "x") }))) { }
        Assert.DoesNotContain(spy.History!, m => m.Role == ChatRole.System);
    }

    [Theory]
    [InlineData(99.0)]
    [InlineData(-5.0)]
    [InlineData(2.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task ClientTemperature_OutOfRange_IsRejected_ProviderNotCalled(double t)
    {
        var (svc, spy) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ChatAsync(new ChatRequestDto("hi", Temperature: t)));
        Assert.Null(spy.Options);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.7)]
    [InlineData(2.0)]
    public async Task ClientTemperature_InRange_PassesThrough(double t)
    {
        var (svc, spy) = Make();
        await svc.ChatAsync(new ChatRequestDto("hi", Temperature: t));
        Assert.Equal(t, spy.Options!.Temperature);
    }

    [Fact]
    public async Task Characterization_TrustedMode_HistoryBeyondCap_DropsOldestIncludingSystem()
    {
        var (svc, spy) = Make(allowClientSystem: true);
        var hist = new List<ChatMessage> { new(ChatRole.System, "first-sys") };
        for (var i = 0; i < 30; i++) hist.Add(new(ChatRole.User, $"u{i}"));
        await svc.ChatAsync(new ChatRequestDto("hi", History: hist));
        Assert.DoesNotContain(spy.History!, m => m.Content == "first-sys");
        Assert.Equal(20, spy.History!.Count);
    }

    private static (ChatService Svc, Spy Spy) MakeWithAllowlist(List<string>? allowed)
    {
        var spy = new Spy();
        var opts = TestOptions.Echo();
        opts.ChatDefaults.AllowedClientProviders = allowed;
        return (new ChatService(new StubChatFactory(spy), new StubTranslationFactory(new Pixous.AI.Providers.EchoTranslationProvider()),
            opts, NullLogger<ChatService>.Instance), spy);
    }

    [Fact]
    public async Task ProviderSelection_DefaultNull_AnyClientProviderReachesFactory_CompatPinned()
    {
        var (svc, spy) = MakeWithAllowlist(null);
        await svc.ChatAsync(new ChatRequestDto("hi", Provider: "AnythingRegistered"));
        Assert.NotNull(spy.Options); // provider invoked
    }

    [Theory]
    [InlineData("Echo")]
    [InlineData("echo")]
    public async Task ProviderSelection_Allowlist_AllowsListedCaseInsensitive(string name)
    {
        var (svc, spy) = MakeWithAllowlist(new() { "Echo" });
        await svc.ChatAsync(new ChatRequestDto("hi", Provider: name));
        Assert.NotNull(spy.Options);
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("")]
    public async Task ProviderSelection_Allowlist_RejectsUnlisted_ProviderNotCalled(string name)
    {
        var (svc, spy) = MakeWithAllowlist(new() { "Echo" });
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ChatAsync(new ChatRequestDto("hi", Provider: name)));
        Assert.Null(spy.Options);
    }

    [Fact]
    public async Task ProviderSelection_EmptyAllowlist_DeniesAnyClientChoice_ButNoProviderFieldStillWorks()
    {
        var (svc, spy) = MakeWithAllowlist(new());
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ChatAsync(new ChatRequestDto("hi", Provider: "Echo")));
        Assert.Null(spy.Options);
        await svc.ChatAsync(new ChatRequestDto("hi"));
        Assert.NotNull(spy.Options);
    }
}
