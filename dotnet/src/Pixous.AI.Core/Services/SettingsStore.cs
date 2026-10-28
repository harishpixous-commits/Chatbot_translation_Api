using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Pixous.AI.Core.Configuration;
using Pixous.AI.Core.Documents;
using Pixous.AI.Core.Security;

namespace Pixous.AI.Core.Services;

/// <summary>
/// File-backed runtime settings: saved LLM API keys (multi-vendor, with
/// auto-fallback health), voice config, uploaded knowledge documents and the
/// settings password hash. Also serves knowledge search (FAQ + uploads) for
/// the KnowledgeBase and MultiLLM providers.
///
/// Storage layout (under <see cref="SettingsOptions.StoreDirectory"/>):
/// <c>settings.json</c> — keys, voice, password hash, document index
/// <c>kb/&lt;id&gt;.json</c> — parsed chunks per uploaded document
///
/// Secrets: API keys live here (memory always; disk only when
/// <see cref="SettingsOptions.AllowPersistKeys"/>). Responses must go through
/// <see cref="MaskKey"/> — the raw key never leaves this class.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _file;
    private readonly string _kbDir;
    private readonly SettingsOptions _options;
    private readonly KnowledgeBaseOptions _kbOptions;
    private readonly ILogger<SettingsStore>? _log;
    private readonly Func<DateTime> _clock;
    private readonly IDataProtector? _protector;
    private readonly bool _blockShippedDefaultPassword;

    /// <summary>Prefix marking a DataProtection-encrypted secret in settings.json.</summary>
    public const string EncryptedPrefix = "v1:enc:";

    private Persisted _data = new();
    private readonly Dictionary<string, List<KnowledgeChunk>> _chunks = new();
    private bool _loaded;

    // Runtime-only per-key health (never persisted — a restart retries every key).
    private readonly ConcurrentDictionary<string, KeyHealth> _health = new();

    // Keys whose stored secret could not be decrypted (different data-protection
    // key ring). FAIL CLOSED, NEVER SILENTLY: the encrypted value is preserved
    // untouched, the key is excluded from use, and the panel shows "locked" so
    // the operator re-enters the secret. Re-entering (update with a new key)
    // unlocks. This is deliberately distinct from an operator deleting a key.
    private readonly ConcurrentDictionary<string, byte> _locked = new();

    private sealed class Persisted
    {
        public string? PasswordHash { get; set; }
        public List<LlmKeyEntry> LlmKeys { get; set; } = new();
        public VoiceSettings Voice { get; set; } = new();
        public List<KnowledgeDocument> Documents { get; set; } = new();
    }

    private sealed class KeyHealth
    {
        public DateTime? CooldownUntilUtc;
        public int? LastStatus;
        public string? LastError;
    }

    private sealed class ChunkFile
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public List<KnowledgeChunk> Chunks { get; set; } = new();
    }

    public SettingsStore(string directory, SettingsOptions options, KnowledgeBaseOptions kbOptions,
        ILogger<SettingsStore>? log = null, Func<DateTime>? clock = null,
        IDataProtector? protector = null, bool blockShippedDefaultPassword = false)
    {
        _protector = protector;
        _blockShippedDefaultPassword = blockShippedDefaultPassword;
        if (options.Enabled && string.Equals(options.Password, "pixous", StringComparison.Ordinal))
            log?.LogWarning(
                "SECURITY: the ⚙ settings panel is using the SHIPPED DEFAULT password. " +
                "Change it now (⚙ panel → Security tab, or AiSuite:Settings:Password via " +
                "User Secrets / env / Key Vault) before this API is reachable by anyone else.");
        _dir = directory;
        _file = Path.Combine(directory, "settings.json");
        _kbDir = Path.Combine(directory, "kb");
        _options = options;
        _kbOptions = kbOptions;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Store directory name (safe to show — never a secret).</summary>
    public string DirectoryName => Path.GetFileName(_dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : _dir;

    // ── load / save ──────────────────────────────────────────────────────
    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(_file))
                {
                    var parsed = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file), Json);
                    if (parsed is not null)
                    {
                        _data = parsed;
                        _data.LlmKeys ??= new();
                        _data.Documents ??= new();
                        _data.Voice ??= new VoiceSettings();
                        for (var i = 0; i < _data.LlmKeys.Count; i++)
                        {
                            _data.LlmKeys[i].Priority = i;
                            _currentLoadKeyId = _data.LlmKeys[i].Id;
                            _data.LlmKeys[i].ApiKey = DecryptForLoad(_data.LlmKeys[i].ApiKey, "LLM key " + _data.LlmKeys[i].Name);
                        }
                        _currentLoadKeyId = null;
                        _data.Voice.SttApiKey = DecryptForLoad(_data.Voice.SttApiKey, "STT key");
                        _data.Voice.TtsApiKey = DecryptForLoad(_data.Voice.TtsApiKey, "TTS key");
                    }
                }
                foreach (var doc in _data.Documents)
                {
                    var path = ChunkPath(doc.Id);
                    if (!File.Exists(path)) continue;
                    var cf = JsonSerializer.Deserialize<ChunkFile>(File.ReadAllText(path), Json);
                    if (cf?.Chunks is { Count: > 0 }) _chunks[doc.Id] = cf.Chunks;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _log?.LogWarning(ex, "Settings store could not be loaded — starting empty.");
                _data = new Persisted();
                _chunks.Clear();
            }
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(_dir);
        RestrictToOwner(_dir, isDir: true);
        if (_data.LlmKeys.Count > 0 && !_options.AllowPersistKeys)
        {
            // Secrets must not touch disk: persist entries without their keys.
            var stripped = new Persisted
            {
                PasswordHash = _data.PasswordHash,
                Voice = StripVoiceKeys(_data.Voice),
                Documents = _data.Documents,
                LlmKeys = _data.LlmKeys.Select(k => { var c = k.Clone(); c.ApiKey = string.Empty; return c; }).ToList()
            };
            AtomicWrite(_file, JsonSerializer.Serialize(stripped, Json));
        }
        else
        {
            AtomicWrite(_file, JsonSerializer.Serialize(ForDisk(_data), Json));
        }
    }

    /// <summary>Clone of the persisted state with every secret encrypted for disk.</summary>
    private Persisted ForDisk(Persisted src)
    {
        if (_protector is null) return src; // unit tests without DataProtection
        return new Persisted
        {
            PasswordHash = src.PasswordHash,
            Documents = src.Documents,
            LlmKeys = src.LlmKeys.Select(k => { var c = k.Clone(); c.ApiKey = EncryptForDisk(c.ApiKey); return c; }).ToList(),
            Voice = EncryptVoice(src.Voice)
        };
    }

    private VoiceSettings EncryptVoice(VoiceSettings v)
    {
        var c = v.Clone();
        c.SttApiKey = EncryptForDisk(c.SttApiKey);
        c.TtsApiKey = EncryptForDisk(c.TtsApiKey);
        return c;
    }

    private string EncryptForDisk(string? value) =>
        string.IsNullOrEmpty(value) || _protector is null || value.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
            ? value ?? string.Empty
            : EncryptedPrefix + _protector.Protect(value);

    /// <summary>
    /// Decrypts a persisted secret. Legacy plaintext passes through (re-encrypted
    /// on next save). A value that cannot be decrypted (different key ring, e.g.
    /// rebuilt container) is dropped with a warning — the operator re-enters it
    /// in the ⚙ panel; we never keep an unusable secret silently.
    /// </summary>
    private string DecryptForLoad(string? value, string label)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return value ?? string.Empty;
        if (_protector is null)
        {
            _log?.LogWarning("Settings store: {Label} is encrypted but no data protector is configured — locked until re-entered.", label);
            if (_currentLoadKeyId is not null) _locked[_currentLoadKeyId] = 1;
            return value;
        }
        try
        {
            return _protector.Unprotect(value[EncryptedPrefix.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _log?.LogWarning(
                "Settings store: {Label} could not be decrypted (different data-protection key ring?). " +
                "The encrypted value is preserved and the key is LOCKED — re-enter the secret in the ⚙ panel to unlock.", label);
            if (_currentLoadKeyId is not null) _locked[_currentLoadKeyId] = 1;
            return value; // preserved verbatim; never overwritten, never used
        }
    }

    [ThreadStatic] private static string? _currentLoadKeyId;

    private static VoiceSettings StripVoiceKeys(VoiceSettings v)
    {
        var c = v.Clone();
        c.SttApiKey = string.Empty;
        c.TtsApiKey = string.Empty;
        return c;
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        RestrictToOwner(tmp, isDir: false);
        File.Move(tmp, path, overwrite: true);
    }

    private string ChunkPath(string id) => Path.Combine(_kbDir, id + ".json");

    // ── password ─────────────────────────────────────────────────────────
    public string? PasswordSource
    {
        get { lock (_gate) { EnsureLoaded(); return _data.PasswordHash is null ? "config" : "stored"; } }
    }

    /// <summary>
    /// True when the panel is enabled, no password was ever changed, the config
    /// still holds the SHIPPED default password and the host blocked it (any
    /// non-Development environment). Login is refused until the operator sets a
    /// real password via env / User Secrets / Key Vault.
    /// </summary>
    public bool IsShippedDefaultPasswordBlocked()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _blockShippedDefaultPassword
                && _data.PasswordHash is null
                && string.Equals(_options.Password, "pixous", StringComparison.Ordinal);
        }
    }

    public bool VerifyPassword(string password)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (IsShippedDefaultPasswordBlocked()) return false;
            if (string.IsNullOrEmpty(password)) return false;
            if (_data.PasswordHash is not null) return PasswordHasher.Verify(password, _data.PasswordHash);
            return PasswordHasher.FixedEquals(password, _options.Password);
        }
    }

    public void SetPassword(string newPassword)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _data.PasswordHash = PasswordHasher.Hash(newPassword);
            SaveLocked();
        }
    }

    // ── LLM keys ─────────────────────────────────────────────────────────
    public IReadOnlyList<LlmKeyEntry> GetKeys()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _data.LlmKeys.OrderBy(k => k.Priority).Select(k => k.Clone()).ToList();
        }
    }

    public LlmKeyEntry? GetKey(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _data.LlmKeys.FirstOrDefault(k => k.Id == id)?.Clone();
        }
    }

    public LlmKeyEntry AddKey(LlmKeyEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("n") : entry.Id;
            entry.Priority = _data.LlmKeys.Count;
            _data.LlmKeys.Add(entry);
            SaveLocked();
            return entry.Clone();
        }
    }

    public LlmKeyEntry? UpdateKey(string id, Action<LlmKeyEntry> mutate)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var key = _data.LlmKeys.FirstOrDefault(k => k.Id == id);
            if (key is null) return null;
            var before = key.ApiKey;
            mutate(key);
            // Operator re-entered the secret → unlock (the old undecryptable blob is replaced).
            if (key.ApiKey != before && !key.ApiKey.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
                _locked.TryRemove(id, out _);
            NormalizePriorities();
            SaveLocked();
            return key.Clone();
        }
    }

    public bool DeleteKey(string id)
    {
        _locked.TryRemove(id, out _);
        lock (_gate)
        {
            EnsureLoaded();
            var removed = _data.LlmKeys.RemoveAll(k => k.Id == id) > 0;
            if (removed)
            {
                NormalizePriorities();
                SaveLocked();
            }
            _health.TryRemove(id, out _);
            return removed;
        }
    }

    public bool MoveKey(string id, string direction)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var ordered = _data.LlmKeys.OrderBy(k => k.Priority).ToList();
            var idx = ordered.FindIndex(k => k.Id == id);
            if (idx < 0) return false;
            var swap = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? idx - 1
                     : direction.Equals("down", StringComparison.OrdinalIgnoreCase) ? idx + 1 : -1;
            if (swap < 0 || swap >= ordered.Count) return true; // already at edge — no-op, not an error
            (ordered[idx].Priority, ordered[swap].Priority) = (ordered[swap].Priority, ordered[idx].Priority);
            NormalizePriorities();
            SaveLocked();
            return true;
        }
    }

    private void NormalizePriorities()
    {
        var i = 0;
        foreach (var k in _data.LlmKeys.OrderBy(k => k.Priority).ToList())
            k.Priority = i++;
    }

    // ── key health (runtime, fallback chain) ─────────────────────────────
    /// <summary>Keys that are enabled and not cooling down, in priority order.</summary>
    public IReadOnlyList<LlmKeyEntry> GetUsableKeys()
    {
        var now = _clock();
        return GetKeys()
            .Where(k => k.Enabled && !IsLocked(k.Id) && !IsCooling(k.Id, now))
            .ToList();
    }

    /// <summary>True when the stored secret could not be decrypted — see _locked.</summary>
    public bool IsLocked(string id) => _locked.ContainsKey(id);

    public bool IsCooling(string id, DateTime? nowUtc = null)
    {
        if (!_health.TryGetValue(id, out var h)) return false;
        return h.CooldownUntilUtc is { } until && (nowUtc ?? _clock()) < until;
    }

    public (string State, DateTime? UntilUtc, int? LastStatus, string? LastError) GetKeyStatus(string id)
    {
        if (IsLocked(id))
            return ("locked", null, null, "Stored secret could not be decrypted (different data-protection key ring?). Re-enter the key to unlock.");
        if (!_health.TryGetValue(id, out var h))
            return (IsVerified(id) ? "ok" : "untested", null, null, null);
        var now = _clock();
        if (h.CooldownUntilUtc is { } until && now < until)
        {
            var state = h.LastStatus switch
            {
                401 => "invalid",   // credentials rejected by the provider
                403 => "denied",    // key recognized but access refused (permission/billing/region)
                _ => "cooling"
            };
            return (state, until, h.LastStatus, h.LastError);
        }
        return (IsVerified(id) ? "ok" : "untested", null, h.LastStatus, h.LastError);
    }

    private bool IsVerified(string id)
    {
        lock (_gate) { EnsureLoaded(); return _data.LlmKeys.FirstOrDefault(k => k.Id == id)?.VerifiedUtc is not null; }
    }

    /// <summary>Marks a key usable again after a successful call, and remembers the first time the
    /// vendor accepted it (persisted) so the UI can distinguish "ok" from "never tested".</summary>
    public void MarkSuccess(string id)
    {
        _health.TryRemove(id, out _);
        lock (_gate)
        {
            EnsureLoaded();
            var entry = _data.LlmKeys.FirstOrDefault(k => k.Id == id);
            if (entry is not null && entry.VerifiedUtc is null)
            {
                entry.VerifiedUtc = _clock();
                SaveLocked();
            }
        }
    }

    /// <summary>Applies a cool-down; the failure kind drives the duration (retryAfterSeconds overrides for 429).</summary>
    public void MarkFailure(string id, int? statusCode, string? error, int? retryAfterSeconds = null)
    {
        var now = _clock();
        var h = _health.GetOrAdd(id, _ => new KeyHealth());
        lock (h)
        {
            h.LastStatus = statusCode;
            // Sanitized at the choke point: no caller can store raw vendor JSON or echoed keys.
            h.LastError = ProviderErrorSanitizer.Sanitize(statusCode, error);
            h.CooldownUntilUtc = statusCode switch
            {
                401 or 403 => now.AddHours(_options.InvalidKeyCooldownHours),
                429 => now.AddSeconds(retryAfterSeconds ?? ResolveRetryAfterSeconds(error) ?? _options.ExhaustedCooldownSeconds),
                408 => null, // timeout — transient, no cool-down
                >= 400 and < 500 => now.AddMinutes(_options.BadConfigCooldownMinutes),
                _ => null // transient 5xx / network — retryable immediately, next key this round only
            };
        }
    }

    /// <summary>Owner-only permissions on Unix (settings hold encrypted secrets).</summary>
    private static void RestrictToOwner(string path, bool isDir)
    {
        if (OperatingSystem.IsWindows()) return; // ACLs are the host's concern on Windows
        try
        {
            File.SetUnixFileMode(path, isDir
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort — a locked-down FS must not break saves */ }
    }

    private static int? ResolveRetryAfterSeconds(string? error)
    {
        // Adapters append "retry-after=Ns" to the error text when the header is present.
        if (error is null) return null;
        const string marker = "retry-after=";
        var i = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = start;
        while (end < error.Length && char.IsDigit(error[end])) end++;
        if (end == start) return null;
        if (int.TryParse(error[start..end], out var s) && s is > 0 and <= 86_400) return s;
        return null;
    }

    // ── voice ────────────────────────────────────────────────────────────
    public VoiceSettings GetVoice()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _data.Voice.Clone();
        }
    }

    public void SetVoice(VoiceSettings voice)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _data.Voice = voice.Clone();
            SaveLocked();
        }
    }

    // ── knowledge documents ──────────────────────────────────────────────
    public IReadOnlyList<KnowledgeDocument> GetDocuments()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _data.Documents.Select(d => new KnowledgeDocument
            {
                Id = d.Id, FileName = d.FileName, SizeBytes = d.SizeBytes,
                ChunkCount = d.ChunkCount, UploadedAtUtc = d.UploadedAtUtc
            }).ToList();
        }
    }

    public KnowledgeDocument AddDocument(string fileName, long sizeBytes, List<KnowledgeChunk> chunks)
    {
        lock (_gate)
        {
            EnsureLoaded();
            // Same file re-uploaded → replace.
            var existing = _data.Documents.FirstOrDefault(d =>
                d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) RemoveDocumentLocked(existing.Id);

            var doc = new KnowledgeDocument
            {
                FileName = fileName,
                SizeBytes = sizeBytes,
                ChunkCount = chunks.Count,
                UploadedAtUtc = _clock()
            };
            _data.Documents.Add(doc);
            _chunks[doc.Id] = chunks;
            Directory.CreateDirectory(_kbDir);
            AtomicWrite(ChunkPath(doc.Id), JsonSerializer.Serialize(new ChunkFile
            {
                Id = doc.Id, FileName = fileName, Chunks = chunks
            }, Json));
            SaveLocked();
            return new KnowledgeDocument
            {
                Id = doc.Id, FileName = doc.FileName, SizeBytes = doc.SizeBytes,
                ChunkCount = doc.ChunkCount, UploadedAtUtc = doc.UploadedAtUtc
            };
        }
    }

    public bool RemoveDocument(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return RemoveDocumentLocked(id);
        }
    }

    private bool RemoveDocumentLocked(string id)
    {
        var removed = _data.Documents.RemoveAll(d => d.Id == id) > 0;
        _chunks.Remove(id);
        try { if (File.Exists(ChunkPath(id))) File.Delete(ChunkPath(id)); }
        catch (IOException) { /* best effort */ }
        if (removed) SaveLocked();
        return removed;
    }

    // ── knowledge search (FAQ + uploads) ─────────────────────────────────
    public IReadOnlyList<KnowledgeHit> Search(string query, int topN = 3, double minScore = 2)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<KnowledgeHit>();
        var tokens = KnowledgeChunker.Tokenize(query);
        if (tokens.Count == 0) return Array.Empty<KnowledgeHit>();
        var phrase = query.Trim().ToLowerInvariant();

        var hits = new List<KnowledgeHit>();

        // FAQ entries — phrase-match scoring (same rule the KnowledgeBase provider uses).
        foreach (var e in _kbOptions.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Answer) || e.Match.Count == 0) continue;
            double score = 0;
            foreach (var m in e.Match)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                if (phrase.Contains(m.Trim(), StringComparison.OrdinalIgnoreCase))
                    score += 1 + m.Trim().Length / 10.0;
            }
            if (score > 0) hits.Add(new KnowledgeHit("faq", e.Answer, score));
        }

        // Uploaded document chunks — token-overlap scoring.
        List<(string Doc, KnowledgeChunk Chunk)> all;
        lock (_gate)
        {
            EnsureLoaded();
            var names = _data.Documents.ToDictionary(d => d.Id, d => d.FileName);
            all = _chunks
                .SelectMany(kv => kv.Value.Select(c => (Doc: names.TryGetValue(kv.Key, out var n) ? n : kv.Key, Chunk: c)))
                .ToList();
        }
        foreach (var (doc, chunk) in all)
        {
            double score = 0;
            var kw = new HashSet<string>(chunk.Keywords, StringComparer.OrdinalIgnoreCase);
            var text = chunk.Text; // contains? — cheap enough at this scale
            foreach (var t in tokens)
            {
                if (kw.Contains(t)) score += 2;
                else if (text.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 1;
            }
            if (score >= minScore) hits.Add(new KnowledgeHit($"upload:{doc}", chunk.Text, score));
        }

        return hits.OrderByDescending(h => h.Score).Take(topN).ToList();
    }

    /// <summary>Masked display form of a secret key: first 4 + … + last 4.</summary>
    public static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (key.Length <= 8) return new string('•', Math.Max(4, key.Length));
        return $"{key[..4]}…{key[^4..]}";
    }
}
