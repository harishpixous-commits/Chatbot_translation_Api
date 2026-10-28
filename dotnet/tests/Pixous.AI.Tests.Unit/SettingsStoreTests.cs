using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Documents;
using Pixous.AI.Core.Security;
using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>Settings store: key CRUD, masking, persistence, health + knowledge search.</summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pixous-store-" + Guid.NewGuid().ToString("n"));
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private SettingsStore NewStore(bool persistKeys = true) => new(
        _dir,
        new SettingsOptions { AllowPersistKeys = persistKeys, ExhaustedCooldownSeconds = 60 },
        new KnowledgeBaseOptions
        {
            Entries = { new KnowledgeEntry { Match = { "our services" }, Answer = "FAQ services answer" } }
        },
        clock: () => _now);

    private static IDataProtector NewProtector() =>
        new ServiceCollection().AddDataProtection().Services
            .BuildServiceProvider().GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Pixous.AI.SettingsStore.v1");

    [Fact]
    public void Keys_PersistEncrypted_WhenProtectorPresent()
    {
        var protector = NewProtector();
        var store = new SettingsStore(_dir,
            new SettingsOptions { AllowPersistKeys = true }, new KnowledgeBaseOptions(),
            clock: () => _now, protector: protector);
        store.AddKey(new LlmKeyEntry { Name = "enc", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = "sk-encrypt-me-12345" });

        var raw = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain("sk-encrypt-me-12345", raw);          // never plaintext on disk
        Assert.Contains(SettingsStore.EncryptedPrefix, raw);

        // Same key ring → round-trips.
        var reloaded = new SettingsStore(_dir,
            new SettingsOptions { AllowPersistKeys = true }, new KnowledgeBaseOptions(),
            clock: () => _now, protector: protector);
        Assert.Equal("sk-encrypt-me-12345", reloaded.GetKeys()[0].ApiKey);
    }

    [Fact]
    public void Keys_Undecryptable_AreLockedPreserved_NotCleared()
    {
        var protector = NewProtector();
        var store = new SettingsStore(_dir,
            new SettingsOptions { AllowPersistKeys = true }, new KnowledgeBaseOptions(),
            clock: () => _now, protector: protector);
        store.AddKey(new LlmKeyEntry { Name = "enc", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = "sk-encrypt-me-12345" });

        // Different key ring (simulated by purpose mismatch): must fail closed
        // VISIBLY — ciphertext preserved, key locked, excluded from use.
        var wrong = new ServiceCollection().AddDataProtection().Services
            .BuildServiceProvider().GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("different-purpose");
        var reloaded = new SettingsStore(_dir,
            new SettingsOptions { AllowPersistKeys = true }, new KnowledgeBaseOptions(),
            clock: () => _now, protector: wrong);

        var key = reloaded.GetKeys()[0];
        Assert.StartsWith(SettingsStore.EncryptedPrefix, key.ApiKey); // preserved, NOT cleared
        Assert.True(reloaded.IsLocked(key.Id));
        Assert.DoesNotContain(reloaded.GetUsableKeys(), k => k.Id == key.Id); // fail closed
        Assert.Equal("locked", reloaded.GetKeyStatus(key.Id).State);

        // The preserved ciphertext is written back untouched (no re-encryption,
        // no loss) when unrelated settings are saved.
        reloaded.SetVoice(new VoiceSettings { SttEnabled = true });
        var raw = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains(SettingsStore.EncryptedPrefix, raw);
        Assert.DoesNotContain("sk-encrypt-me-12345", raw);

        // Operator re-enters the secret → unlocked and usable again.
        var updated = reloaded.UpdateKey(key.Id, k => k.ApiKey = "sk-reentered-999");
        Assert.NotNull(updated);
        Assert.False(reloaded.IsLocked(key.Id));
        Assert.Contains(reloaded.GetUsableKeys(), k => k.Id == key.Id && k.ApiKey == "sk-reentered-999");
    }

    [Fact]
    public void Persisted_Files_Are_OwnerOnly_On_Unix()
    {
        if (OperatingSystem.IsWindows()) return; // ACLs are the host's concern there
        var store = NewStore();
        store.AddKey(new LlmKeyEntry { Name = "k", Vendor = "v", Endpoint = "https://a/v1", Model = "m", ApiKey = "sk-secret-perms" });
        var dirMode = File.GetUnixFileMode(_dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, dirMode);
        var fileMode = File.GetUnixFileMode(Path.Combine(_dir, "settings.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
    }

    [Fact]
    public void KeyRingGuard_Restricts_KeyRing_And_Warns_Inside_Store()
    {
        if (OperatingSystem.IsWindows()) return;
        var ring = Path.Combine(_dir, "ring");
        Directory.CreateDirectory(ring);
        var keyFile = Path.Combine(ring, "key-test.xml");
        File.WriteAllText(keyFile, "<key/>");
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Pixous.AI.AspNetCore.Extensions.KeyRingGuard.Secure(ring, Path.Combine(_dir, "store"), null);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(ring));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(keyFile));
    }

    [Fact]
    public void RateLimit_Is_On_By_Default_SafeDeployment()
    {
        Assert.True(new Pixous.AI.Core.Configuration.RateLimitOptions().Enabled);
    }

    [Fact]
    public void KeyStatus_Distinguishes_Invalid_Denied_Cooling()
    {
        var store = NewStore();
        var key = store.AddKey(new LlmKeyEntry { Name = "k", Vendor = "v", Endpoint = "https://a/v1", Model = "m", ApiKey = "sk-x" });

        store.MarkFailure(key.Id, 401, "bad key");
        Assert.Equal("invalid", store.GetKeyStatus(key.Id).State);

        store.MarkFailure(key.Id, 403, "billing not enabled");
        Assert.Equal("denied", store.GetKeyStatus(key.Id).State);

        store.MarkFailure(key.Id, 429, "rate limited", retryAfterSeconds: 30);
        var st = store.GetKeyStatus(key.Id);
        Assert.Equal("cooling", st.State);
        Assert.Equal(429, st.LastStatus);

        store.MarkSuccess(key.Id);
        Assert.Equal("ok", store.GetKeyStatus(key.Id).State);
    }

    [Fact]
    public void ShippedDefaultPassword_Blocked_WhenFlagSet()
    {
        var blocked = new SettingsStore(_dir,
            new SettingsOptions { Enabled = true, Password = "pixous" }, new KnowledgeBaseOptions(),
            clock: () => _now, blockShippedDefaultPassword: true);
        Assert.True(blocked.IsShippedDefaultPasswordBlocked());
        Assert.False(blocked.VerifyPassword("pixous"));

        // After a real password is set (simulating env/config), block lifts.
        var custom = new SettingsStore(_dir,
            new SettingsOptions { Enabled = true, Password = "real-pass-from-env" }, new KnowledgeBaseOptions(),
            clock: () => _now, blockShippedDefaultPassword: true);
        Assert.False(custom.IsShippedDefaultPasswordBlocked());
        Assert.True(custom.VerifyPassword("real-pass-from-env"));

        // Development-style (flag off): default still works.
        var dev = new SettingsStore(_dir,
            new SettingsOptions { Enabled = true, Password = "pixous" }, new KnowledgeBaseOptions(),
            clock: () => _now);
        Assert.False(dev.IsShippedDefaultPasswordBlocked());
        Assert.True(dev.VerifyPassword("pixous"));
    }

    [Fact]
    public void KeyCrud_OrderMoveDelete_Works()
    {
        var store = NewStore();
        var a = store.AddKey(new LlmKeyEntry { Name = "a", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = "k1" });
        var b = store.AddKey(new LlmKeyEntry { Name = "b", Vendor = "OpenAI", Endpoint = "https://b/v1", Model = "m", ApiKey = "k2" });
        var c = store.AddKey(new LlmKeyEntry { Name = "c", Vendor = "OpenAI", Endpoint = "https://c/v1", Model = "m", ApiKey = "k3" });

        Assert.Equal(new[] { "a", "b", "c" }, store.GetKeys().Select(k => k.Name));

        // Move "c" up twice → first.
        Assert.True(store.MoveKey(c.Id, "up"));
        Assert.True(store.MoveKey(c.Id, "up"));
        Assert.Equal(new[] { "c", "a", "b" }, store.GetKeys().Select(k => k.Name));

        // Move at the edge = no-op, not an error.
        Assert.True(store.MoveKey(c.Id, "up"));
        Assert.Equal("c", store.GetKeys()[0].Name);

        // Update keeps identity.
        var updated = store.UpdateKey(a.Id, k => k.Model = "m-updated");
        Assert.NotNull(updated);
        Assert.Equal(a.Id, updated!.Id);
        Assert.Equal("m-updated", store.GetKey(a.Id)!.Model);

        Assert.True(store.DeleteKey(a.Id));
        Assert.False(store.DeleteKey(a.Id));
        Assert.Equal(new[] { "c", "b" }, store.GetKeys().Select(k => k.Name));
        // Priorities normalized after delete.
        Assert.Equal(0, store.GetKeys()[0].Priority);
        Assert.Equal(1, store.GetKeys()[1].Priority);
    }

    [Fact]
    public void RawKey_NeverReturned_ViaMaskHelper()
    {
        var store = NewStore();
        const string secret = "sk-live-abcd1234efgh5678";
        store.AddKey(new LlmKeyEntry { Name = "x", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = secret });

        var masked = SettingsStore.MaskKey(secret);
        Assert.Equal("sk-l…5678", masked);
        Assert.DoesNotContain(secret, masked);

        var entry = store.GetKeys()[0];
        Assert.Equal(secret, entry.ApiKey); // raw available server-side only
        Assert.DoesNotContain("…", entry.ApiKey);
    }

    [Fact]
    public void Keys_PersistToDisk_AndReload()
    {
        var store = NewStore();
        store.AddKey(new LlmKeyEntry { Name = "keep", Vendor = "Anthropic", Endpoint = "https://api.anthropic.com", Model = "m", ApiKey = "sk-secret-key-123" });

        var reloaded = NewStore();
        var key = Assert.Single(reloaded.GetKeys());
        Assert.Equal("keep", key.Name);
        Assert.Equal("sk-secret-key-123", key.ApiKey);
        Assert.Contains("sk-secret-key-123", File.ReadAllText(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void PersistKeysOff_SecretsNeverTouchDisk()
    {
        var store = NewStore(persistKeys: false);
        store.AddKey(new LlmKeyEntry { Name = "mem", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = "sk-mem-only-9999" });

        Assert.Equal("sk-mem-only-9999", store.GetKeys()[0].ApiKey); // usable in memory
        var onDisk = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain("sk-mem-only-9999", onDisk);
    }

    [Fact]
    public void Health_429Cools_SkipsUsable_SuccessClears()
    {
        var store = NewStore();
        var k = store.AddKey(new LlmKeyEntry { Name = "h", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = "k" });

        store.MarkFailure(k.Id, 429, "rate limited", retryAfterSeconds: 7);
        Assert.True(store.IsCooling(k.Id));
        Assert.Empty(store.GetUsableKeys());
        var (state, until, status, _) = store.GetKeyStatus(k.Id);
        Assert.Equal("cooling", state);
        Assert.Equal(429, status);
        Assert.NotNull(until);

        _now = _now.AddSeconds(8);
        Assert.False(store.IsCooling(k.Id));
        Assert.Single(store.GetUsableKeys());

        store.MarkSuccess(k.Id);
        Assert.Equal("ok", store.GetKeyStatus(k.Id).State);
    }

    [Fact]
    public void Health_5xx_NoCooldown()
    {
        var store = NewStore();
        var k = store.AddKey(new LlmKeyEntry { Name = "h", Vendor = "OpenAI", Endpoint = "https://a/v1", Model = "m", ApiKey = "k" });
        store.MarkFailure(k.Id, 503, "unavailable");
        Assert.False(store.IsCooling(k.Id));
        Assert.Single(store.GetUsableKeys());
    }

    [Fact]
    public void Search_FindsFaq_AndUploadedChunks()
    {
        var store = NewStore();
        store.AddDocument("guide.md", 50, new List<KnowledgeChunk>
        {
            new()
            {
                Text = "The refund window lasts thirty days from purchase date.",
                Keywords = new List<string> { "refund", "window", "thirty", "days", "purchase" }
            }
        });

        var faq = store.Search("can you tell me about our services", topN: 3);
        Assert.Contains(faq, h => h.Source == "faq");

        var doc = store.Search("what is the refund window", topN: 3);
        var hit = doc.First(h => h.Source == "upload:guide.md");
        Assert.Contains("thirty days", hit.Text);

        Assert.Empty(store.Search(""));           // no query
        Assert.Empty(store.Search("zebra xylophon")); // no matches
    }

    [Fact]
    public void DocumentReplace_SameFileName_KeepsSingleEntry()
    {
        var store = NewStore();
        store.AddDocument("dup.md", 10, new List<KnowledgeChunk> { new() { Text = "first version", Keywords = { "first" } } });
        store.AddDocument("dup.md", 12, new List<KnowledgeChunk> { new() { Text = "second version", Keywords = { "second" } } });

        var doc = Assert.Single(store.GetDocuments());
        Assert.Equal(1, doc.ChunkCount);
        Assert.Contains("second", store.Search("second version", 1, minScore: 1)[0].Text);
    }

    [Fact]
    public void Documents_PersistAcrossReload_ThenDelete()
    {
        var store = NewStore();
        var doc = store.AddDocument("keep.md", 10, new List<KnowledgeChunk>
        {
            new() { Text = "Persistent content about gorillas.", Keywords = { "gorillas", "persistent" } }
        });

        var reloaded = NewStore();
        Assert.Contains(reloaded.GetDocuments(), d => d.Id == doc.Id);
        Assert.NotEmpty(reloaded.Search("persistent gorillas", 1, minScore: 1));

        Assert.True(reloaded.RemoveDocument(doc.Id));
        Assert.Empty(reloaded.GetDocuments());
        Assert.Empty(reloaded.Search("persistent gorillas", 1, minScore: 1));

        var third = NewStore();
        Assert.Empty(third.GetDocuments());
    }

    [Fact]
    public void Voice_SavedAndMaskedReload()
    {
        var store = NewStore();
        store.SetVoice(new VoiceSettings
        {
            SttEnabled = true, SttProvider = VoiceSettings.OpenAiCompatible, SttApiKey = "stt-key-123",
            TtsEnabled = true, TtsProvider = VoiceSettings.OpenAiCompatible, TtsApiKey = "tts-key-456", TtsVoice = "nova"
        });

        var reloaded = NewStore();
        var v = reloaded.GetVoice();
        Assert.True(v.SttEnabled);
        Assert.Equal("stt-key-123", v.SttApiKey);
        Assert.Equal("nova", v.TtsVoice);
        // Voice keys are masked at the endpoint layer — store file holds them raw (documented).
        Assert.Contains("stt-key-123", File.ReadAllText(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void Password_ConfigFallback_AndStoredHashOverride()
    {
        var store = new SettingsStore(_dir, new SettingsOptions { Password = "config-pass" }, new KnowledgeBaseOptions());

        Assert.Equal("config", store.PasswordSource);
        Assert.True(store.VerifyPassword("config-pass"));
        Assert.False(store.VerifyPassword("wrong"));

        store.SetPassword("changed-pass-1");
        Assert.Equal("stored", store.PasswordSource);
        Assert.True(store.VerifyPassword("changed-pass-1"));
        Assert.False(store.VerifyPassword("config-pass")); // config no longer accepted

        // Hash survives reload.
        var reloaded = new SettingsStore(_dir, new SettingsOptions { Password = "config-pass" }, new KnowledgeBaseOptions());
        Assert.True(reloaded.VerifyPassword("changed-pass-1"));
    }
}

/// <summary>Password hashing + settings session tokens + login lockout.</summary>
public class SettingsSecurityTests
{
    [Fact]
    public void PasswordHasher_Roundtrip()
    {
        var hash = PasswordHasher.Hash("s3cret-password");
        Assert.NotEqual("s3cret-password", hash);
        Assert.True(PasswordHasher.Verify("s3cret-password", hash));
        Assert.False(PasswordHasher.Verify("S3cret-password", hash));
        Assert.False(PasswordHasher.Verify("s3cret-password", "garbage"));
        Assert.False(PasswordHasher.Verify("", hash));
        // Unique salts → different hashes for the same input.
        Assert.NotEqual(hash, PasswordHasher.Hash("s3cret-password"));
    }

    [Fact]
    public void Tokens_IssueValidateExpireRevoke()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new SettingsTokenService(() => now);
        var opts = new SettingsOptions { TokenHours = 8 };

        var (token, expires) = svc.Issue(opts);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(svc.Validate(token));

        now = now.AddHours(7);
        Assert.True(svc.Validate(token));
        now = now.AddHours(2); // 9h later — expired
        Assert.False(svc.Validate(token));

        var (token2, _) = svc.Issue(opts);
        svc.Revoke(token2);
        Assert.False(svc.Validate(token2));
        Assert.False(svc.Validate(null));
        Assert.False(svc.Validate(""));
    }

    [Fact]
    public void LoginLockout_After5Failures_ThenRecovers()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new SettingsTokenService(() => now);
        const string caller = "10.0.0.1";

        for (var i = 0; i < 4; i++)
            Assert.Equal(TimeSpan.Zero, svc.RegisterFailure(caller));

        var lockout = svc.RegisterFailure(caller); // 5th failure locks
        Assert.True(lockout > TimeSpan.Zero);

        Assert.True(svc.RemainingLockout(caller) > TimeSpan.Zero);
        now = now.AddSeconds(61);
        Assert.Equal(TimeSpan.Zero, svc.RemainingLockout(caller));

        // A successful login clears the counter.
        svc.RegisterFailure(caller);
        svc.RegisterSuccess(caller);
        Assert.Equal(TimeSpan.Zero, svc.RemainingLockout(caller));
    }
}

public class KeyVerificationStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aisuite-verified-" + Guid.NewGuid().ToString("n"));
    private SettingsStore NewStore() => new(_dir, new SettingsOptions { AllowPersistKeys = true },
        new KnowledgeBaseOptions(), clock: () => DateTime.UtcNow);

    [Fact]
    public void Fresh_key_is_untested_not_ok()
    {
        var store = NewStore();
        var key = store.AddKey(new LlmKeyEntry { Name = "k", Vendor = "OpenAI", ApiKey = "sk-fixture" });
        Assert.Equal("untested", store.GetKeyStatus(key.Id).State);
    }

    [Fact]
    public void Transient_failure_without_cooldown_stays_untested()
    {
        var store = NewStore();
        var key = store.AddKey(new LlmKeyEntry { Name = "k", Vendor = "OpenAI", ApiKey = "sk-fixture" });
        store.MarkFailure(key.Id, 500, "boom");
        Assert.Equal("untested", store.GetKeyStatus(key.Id).State);
    }

    [Fact]
    public void Success_flips_to_ok_and_persists_across_reload()
    {
        string id;
        {
            var store = NewStore();
            id = store.AddKey(new LlmKeyEntry { Name = "k", Vendor = "OpenAI", ApiKey = "sk-fixture" }).Id;
            store.MarkSuccess(id);
            Assert.Equal("ok", store.GetKeyStatus(id).State);
        }
        var reloaded = NewStore();
        Assert.Equal("ok", reloaded.GetKeyStatus(id).State);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
