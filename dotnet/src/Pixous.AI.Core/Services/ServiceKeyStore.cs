using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Pixous.AI.Core.Services;

/// <summary>Public metadata about a project service key. The raw key is never stored or returned after creation.</summary>
public sealed record ServiceKeyInfo(
    string Id, string Name, string Prefix, DateTime CreatedUtc,
    DateTime? RevokedUtc, long TotalRequests, DateTime? LastUsedUtc);

/// <summary>
/// File-backed store for per-project service keys (the "API key" a consumer
/// project puts in its own backend to call the shared <c>/api/ai/v1</c>
/// surface). Raw keys are shown ONCE at creation/rotation; only a SHA-256
/// hash is persisted (service-keys.json, mode 0600 next to settings.json).
/// Keys are validated with a fixed-time comparison. Per-key rate limiting is
/// a fixed one-minute window kept in memory (resets on restart).
/// </summary>
public sealed class ServiceKeyStore
{
    public const int DefaultPerMinuteLimit = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class Persisted
    {
        public List<Entry> Keys { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Prefix { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime? RevokedUtc { get; set; }
        public long TotalRequests { get; set; }
        public DateTime? LastUsedUtc { get; set; }
    }

    private sealed class Window
    {
        public DateTime MinuteStart;
        public int Count;
    }

    private readonly object _gate = new();
    private readonly string _file;
    private readonly ILogger<ServiceKeyStore>? _log;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, Window> _windows = new();
    private Persisted _data = new();
    private bool _loaded;

    public ServiceKeyStore(string settingsDir, ILogger<ServiceKeyStore>? log = null, Func<DateTime>? clock = null)
    {
        _file = Path.Combine(settingsDir, "service-keys.json");
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Creates a key. Returns the RAW key exactly once — it is never stored.</summary>
    public (string RawKey, ServiceKeyInfo Info) Create(string name)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var raw = "pk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_');
            var entry = new Entry
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = string.IsNullOrWhiteSpace(name) ? "project" : name.Trim(),
                Hash = Sha256(raw),
                Prefix = raw[..10],
                CreatedUtc = _clock()
            };
            _data.Keys.Add(entry);
            Save();
            return (raw, ToInfo(entry));
        }
    }

    public IReadOnlyList<ServiceKeyInfo> List()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _data.Keys.OrderByDescending(k => k.CreatedUtc).Select(ToInfo).ToList();
        }
    }

    public bool Revoke(string id) => SetRevoked(id, true);

    /// <summary>Rotates: old raw key stops working immediately; the new raw key is returned once.</summary>
    public (string RawKey, ServiceKeyInfo Info)? Rotate(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var entry = _data.Keys.FirstOrDefault(k => k.Id == id);
            if (entry is null) return null;
            var raw = "pk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_');
            entry.Hash = Sha256(raw);
            entry.Prefix = raw[..10];
            entry.RevokedUtc = null;
            Save();
            lock (_windows) _windows.Remove(id);
            return (raw, ToInfo(entry));
        }
    }

    /// <summary>Validates a raw key (fixed-time hash compare, not revoked). Returns null when invalid.</summary>
    public ServiceKeyInfo? Validate(string? rawKey)
    {
        if (string.IsNullOrEmpty(rawKey)) return null;
        lock (_gate)
        {
            EnsureLoaded();
            var hash = Sha256(rawKey);
            var match = _data.Keys.FirstOrDefault(k =>
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(k.Hash),
                    System.Text.Encoding.ASCII.GetBytes(hash)));
            if (match is null || match.RevokedUtc is not null) return null;
            return ToInfo(match);
        }
    }

    /// <summary>
    /// Fixed-window per-key rate permit (default 60/min). Counts usage only
    /// when permitted, so rejected bursts do not extend the window.
    /// </summary>
    public bool TryAcquirePermit(string keyId, int perMinuteLimit = DefaultPerMinuteLimit)
    {
        var now = _clock();
        lock (_windows)
        {
            if (!_windows.TryGetValue(keyId, out var w) || now - w.MinuteStart >= TimeSpan.FromMinutes(1))
            {
                w = new Window { MinuteStart = now, Count = 0 };
                _windows[keyId] = w;
            }
            if (w.Count >= perMinuteLimit) return false;
            w.Count++;
            return true;
        }
    }

    /// <summary>Records one served request on the key (usage counters).</summary>
    public void RecordUsage(string keyId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var entry = _data.Keys.FirstOrDefault(k => k.Id == keyId);
            if (entry is null) return;
            entry.TotalRequests++;
            entry.LastUsedUtc = _clock();
            Save();
        }
    }

    private bool SetRevoked(string id, bool revoked)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var entry = _data.Keys.FirstOrDefault(k => k.Id == id);
            if (entry is null) return false;
            entry.RevokedUtc = revoked ? _clock() : null;
            Save();
            return true;
        }
    }

    private static ServiceKeyInfo ToInfo(Entry e) =>
        new(e.Id, e.Name, e.Prefix, e.CreatedUtc, e.RevokedUtc, e.TotalRequests, e.LastUsedUtc);

    private static string Sha256(string raw) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));

    private void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(_file))
                _data = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file), Json) ?? new Persisted();
        }
        catch (Exception ex)
        {
            // FAIL CLOSED: an unreadable key file must not silently become "no
            // keys exist" while the file is actually intact — but refusing all
            // service (throwing) would also break chat for everyone. Keep the
            // file untouched and treat every key as invalid until fixed.
            _log?.LogError(ex, "service-keys.json could not be read; all service keys will be rejected until it is fixed.");
            _data = new Persisted();
        }
        _loaded = true;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* Windows */ }
        File.Move(tmp, _file, overwrite: true);
    }
}
