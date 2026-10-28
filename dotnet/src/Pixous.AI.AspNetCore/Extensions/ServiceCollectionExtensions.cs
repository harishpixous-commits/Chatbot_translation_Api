using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Pixous.AI.Abstractions;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Services;
using Pixous.AI.Core.Stores;
using Pixous.AI.AspNetCore.Security;
using Pixous.AI.Providers;

namespace Pixous.AI.AspNetCore.Extensions;

// ── Factories (precedence: per-request → runtime selection → configuration) ─
internal sealed class ChatProviderFactory : IChatProviderFactory
{
    private readonly IServiceProvider _sp;
    private readonly AiSuiteOptions _options;
    private readonly AiSuiteRuntimeSelection _selection;
    public ChatProviderFactory(IServiceProvider sp, AiSuiteOptions options, AiSuiteRuntimeSelection selection)
    { _sp = sp; _options = options; _selection = selection; }

    public IChatProvider Get(string? providerName = null)
    {
        var name = providerName ?? _selection.ChatProvider ?? _options.ChatProvider;
        var match = _sp.GetServices<IChatProvider>()
            .FirstOrDefault(p => p.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"Chat provider '{name}' is not registered. Registered: {string.Join(", ", RegisteredProviders)}. " +
            $"Check AiSuite:ChatProvider in configuration or POST /api/ai/selection.");
    }

    public IEnumerable<string> RegisteredProviders =>
        _sp.GetServices<IChatProvider>().Select(p => p.ProviderName);
}

internal sealed class TranslationProviderFactory : ITranslationProviderFactory
{
    private readonly IServiceProvider _sp;
    private readonly AiSuiteOptions _options;
    private readonly AiSuiteRuntimeSelection _selection;
    public TranslationProviderFactory(IServiceProvider sp, AiSuiteOptions options, AiSuiteRuntimeSelection selection)
    { _sp = sp; _options = options; _selection = selection; }

    public ITranslationProvider Get(string? providerName = null)
    {
        var name = providerName ?? _selection.TranslationProvider ?? _options.TranslationProvider;
        var match = _sp.GetServices<ITranslationProvider>()
            .FirstOrDefault(p => p.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"Translation provider '{name}' is not registered. Registered: {string.Join(", ", RegisteredProviders)}. " +
            $"Check AiSuite:TranslationProvider in configuration or POST /api/ai/selection.");
    }

    public IEnumerable<string> RegisteredProviders =>
        _sp.GetServices<ITranslationProvider>().Select(p => p.ProviderName);
}

// ── Registration ─────────────────────────────────────────────────────────
/// <summary>
/// Plug-and-play registration:
/// <c>builder.Services.AddAiSuite(builder.Configuration);</c>
/// then <c>app.MapAiSuiteEndpoints();</c> (and <c>app.UseRateLimiter();</c>
/// if rate limiting is enabled). Secrets are read from
/// <see cref="IConfiguration"/> (User Secrets / env / Key Vault) — never
/// from appsettings files and never hard-coded.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAiSuite(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = AiSuiteOptions.SectionName)
    {
        var options = new AiSuiteOptions();
        configuration.GetSection(sectionName).Bind(options);

        // Configuration Binder APPENDS collections to code defaults —
        // de-duplicate before validation.
        options.Languages.Supported = options.Languages.Supported
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = options.Validate().ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid AiSuite configuration:\n - " + string.Join("\n - ", errors));

        // ChatMessage.Role is an enum: REST clients (demo page, Angular library,
        // any caller) send string roles like { "role": "user" }. Without this
        // converter every request carrying a non-empty history fails to bind.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddSingleton(options);
        services.AddSingleton<AiSuiteRuntimeSelection>();

        // ── Runtime settings store (⚙ panel): saved LLM keys, voice, uploads ──
        // Directory: Settings:StoreDirectory (relative → content root).
        // Secrets are encrypted at rest with ASP.NET Core Data Protection
        // (shared framework, no extra packages). Default key ring lives under the
        // user profile / container: multi-instance or ephemeral hosts must
        // configure a shared key ring (see docs/security.md) or re-enter keys.
        // Key ring: explicit AiSuite:DataProtection:KeyRingDirectory → persisted
        // there and hardened (0700 dir / 0600 files on Unix, startup warning if
        // inside the settings store). Null → framework default location (see the
        // option's docs): fine for one persistent host, wrong for multi-instance
        // or ephemeral hosts unless the host configures its own shared storage.
        var dp = services.AddDataProtection();
        var keyRingDir = options.DataProtection.KeyRingDirectory;
        if (!string.IsNullOrWhiteSpace(keyRingDir))
            dp.PersistKeysToFileSystem(new DirectoryInfo(keyRingDir));
        services.AddSingleton(sp =>
        {
            var env = sp.GetService<IHostEnvironment>();
            var dir = options.Settings.StoreDirectory;
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(env?.ContentRootPath ?? Directory.GetCurrentDirectory(), dir);
            if (!string.IsNullOrWhiteSpace(keyRingDir))
                KeyRingGuard.Secure(keyRingDir, dir, sp.GetService<ILogger<SettingsStore>>());
            var protector = sp.GetService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                ?.CreateProtector("Pixous.AI.SettingsStore.v1");
            // Secure first setup: outside Development the shipped default password
            // ("pixous") is refused until the operator sets a real one via
            // AiSuite__Settings__Password (env / User Secrets / Key Vault).
            var blockDefault = options.Settings.Enabled && env?.IsDevelopment() != true;
            return new SettingsStore(dir, options.Settings, options.KnowledgeBase,
                sp.GetService<ILogger<SettingsStore>>(),
                protector: protector, blockShippedDefaultPassword: blockDefault);
        });
        services.AddSingleton<SettingsTokenService>();

        // Project service keys for the shared /api/ai/v1 surface: same store
        // directory as settings, separate file (service-keys.json, 0600),
        // only SHA-256 hashes persisted.
        services.AddSingleton(sp =>
        {
            var env = sp.GetService<IHostEnvironment>();
            var dir = options.Settings.StoreDirectory;
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(env?.ContentRootPath ?? Directory.GetCurrentDirectory(), dir);
            return new ServiceKeyStore(dir, sp.GetService<ILogger<ServiceKeyStore>>());
        });

        // Keyless providers: always available (dev, tests, safe fallback).
        services.AddSingleton<IChatProvider, EchoChatProvider>();
        services.AddSingleton<IChatProvider>(sp =>
            new KnowledgeBaseChatProvider(options.KnowledgeBase, sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton<ITranslationProvider, EchoTranslationProvider>();

        // Multi-vendor, multi-key fallback chain — always registered so it can be
        // selected; fails with a clear message until keys are saved in Settings.
        // Infinite client timeout: per-call timeouts are enforced by
        // ProviderResilience (headers phase); a long SSE stream must not be
        // cut by a blanket client timeout.
        services.AddHttpClient(MultiLlmChatProvider.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddSingleton<IChatProvider>(sp => new MultiLlmChatProvider(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            options.Resilience,
            sp.GetService<ILogger<MultiLlmChatProvider>>()));

        // Voice (STT/TTS) — provider picked at runtime from the Settings voice config.
        services.AddHttpClient(VoiceService.HttpClientName);
        services.AddSingleton<IVoiceService>(sp => new VoiceService(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            options.Resilience));

        // Real providers: registered only when configured.
        if (!string.IsNullOrWhiteSpace(options.OpenAiCompatible.Endpoint))
        {
            var chatKey = configuration[options.OpenAiCompatible.ApiKeyConfigKey];
            var chatOpts = options.OpenAiCompatible;
            var resilience = options.Resilience;
            services.AddHttpClient(nameof(OpenAiCompatibleChatProvider))
                .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
            services.AddSingleton<IChatProvider>(sp =>
                new OpenAiCompatibleChatProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenAiCompatibleChatProvider)),
                    chatOpts, resilience, chatKey));
        }
        if (!string.IsNullOrWhiteSpace(options.AzureTranslator.Endpoint) &&
            !string.IsNullOrWhiteSpace(options.AzureTranslator.Region))
        {
            var trKey = configuration[options.AzureTranslator.ApiKeyConfigKey];
            var trOpts = options.AzureTranslator;
            var resilience = options.Resilience;
            services.AddHttpClient(nameof(AzureTranslatorProvider));
            services.AddSingleton<ITranslationProvider>(sp =>
                new AzureTranslatorProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AzureTranslatorProvider)),
                    trOpts, resilience, trKey));
        }
        if (!string.IsNullOrWhiteSpace(options.LibreTranslate.Endpoint))
        {
            var libreOpts = options.LibreTranslate;
            var resilience = options.Resilience;
            var libreKey = libreOpts.ApiKeyConfigKey is null ? null : configuration[libreOpts.ApiKeyConfigKey];
            services.AddHttpClient(nameof(LibreTranslateProvider));
            services.AddSingleton<ITranslationProvider>(sp =>
                new LibreTranslateProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LibreTranslateProvider)),
                    libreOpts, resilience, libreKey));
        }

        if (!string.IsNullOrWhiteSpace(options.PixousEngine.Endpoint))
        {
            var pxOpts = options.PixousEngine;
            var pxResilience = options.Resilience;
            var pxToken = pxOpts.TokenConfigKey is null ? null : configuration[pxOpts.TokenConfigKey];
            services.AddHttpClient(nameof(PixousSelfHostedTranslationProvider));
            services.AddSingleton<ITranslationProvider>(sp =>
                new PixousSelfHostedTranslationProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PixousSelfHostedTranslationProvider)),
                    pxOpts, pxResilience, pxToken));
        }

        services.AddSingleton<IChatProviderFactory, ChatProviderFactory>();
        services.AddSingleton<ITranslationProviderFactory, TranslationProviderFactory>();

        // Conversation store: default in-memory; the host may register its own
        // (SQL/Mongo/Redis) BEFORE calling AddAiSuite and it will be kept.
        if (!services.Any(s => s.ServiceType == typeof(IConversationStore)))
            services.AddSingleton<IConversationStore, InMemoryConversationStore>();

        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<ITranslationService, TranslationService>();

        // Built-in rate limiting (framework only, no extra packages).
        // Partitioned per caller IP: one visitor cannot exhaust everyone else's
        // budget, and the cap applies to the public quota-spending endpoints.
        if (options.RateLimit.Enabled)
        {
            var rl = options.RateLimit;
            services.AddRateLimiter(o =>
            {
                o.AddPolicy("pixous-ai", http =>
                    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                        {
                            PermitLimit = rl.PermitLimit,
                            Window = TimeSpan.FromSeconds(rl.WindowSeconds),
                            QueueLimit = 0
                        }));
                // Honest rejection: 429 (not 503) + Retry-After when the window resets.
                o.OnRejected = (ctx, _) =>
                {
                    ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.HttpContext.Response.Headers.RetryAfter = rl.WindowSeconds.ToString();
                    return ValueTask.CompletedTask;
                };
            });
        }


        // Built-in API-key auth (optional): lets a self-hosted host flip
        // Security:RequireAuthorization on without an IdP. JWT/OIDC hosts register
        // their own scheme instead; either satisfies the endpoints' authorization.
        if (options.Security.RequireAuthorization)
        {
            // Authorization services are required by UseAuthorization regardless of
            // which scheme the host uses (TryAdd semantics make this safe to repeat).
            services.AddAuthorization();
            if (options.Security.ApiKeys.Length > 0)
            {
                var sec = options.Security;
                services.AddAuthentication(Security.ApiKeyAuthenticationHandler.SchemeName)
                    .AddScheme<Security.ApiKeyAuthenticationOptions, Security.ApiKeyAuthenticationHandler>(
                        Security.ApiKeyAuthenticationHandler.SchemeName, o => o.Keys = sec.ApiKeys);
            }
        }

        // Exact-origin CORS (optional, off by default). Validation has already
        // rejected '*' so WithOrigins can never become an implicit allow-all.
        if (options.Cors.Enabled)
        {
            var cors = options.Cors;
            services.AddCors(o => o.AddPolicy(Hosting.AiSuiteEndpoints.CorsPolicyName, p =>
            {
                p.WithOrigins(cors.AllowedOrigins)
                 .WithMethods("GET", "POST", "OPTIONS")
                 .WithHeaders("Content-Type", "Authorization", "X-API-Key", "X-Settings-Token");
                if (cors.AllowCredentials) p.AllowCredentials();
            }));
        }

        return services;
    }
}

/// <summary>
/// Hardens the data-protection key ring at rest: owner-only permissions on Unix
/// and a loud warning when the ring sits inside the settings store directory
/// (master keys must never live beside the secrets they encrypt).
/// </summary>
public static class KeyRingGuard
{
    public static void Secure(string keyRingDir, string storeDir, ILogger? log)
    {
        try
        {
            var fullRing = Path.GetFullPath(keyRingDir);
            var fullStore = Path.GetFullPath(storeDir);
            if (fullRing.StartsWith(fullStore + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullRing.Equals(fullStore, StringComparison.OrdinalIgnoreCase))
            {
                log?.LogWarning(
                    "Data Protection key ring '{Ring}' is inside the settings store '{Store}'. " +
                    "Master keys must live separately — move AiSuite:DataProtection:KeyRingDirectory outside it.",
                    fullRing, fullStore);
            }

            if (OperatingSystem.IsWindows()) return; // ACLs are the host's concern on Windows

            Directory.CreateDirectory(fullRing);
            File.SetUnixFileMode(fullRing, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var f in Directory.EnumerateFiles(fullRing, "*.xml"))
            {
                var mode = File.GetUnixFileMode(f);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
                    log?.LogWarning("Data Protection key file '{File}' was group/world-readable — restricted to owner-only.", f);
                File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not harden key-ring permissions; check directory ACLs manually.");
        }
    }
}
