using System.Collections.Concurrent;
using System.Security.Cryptography;
using Pixous.AI.Core.Configuration;

namespace Pixous.AI.Core.Services;

/// <summary>
/// Issues and validates short-lived Settings session tokens (header
/// <c>X-Settings-Token</c> or <c>Authorization: Bearer</c>). Also applies a
/// small per-caller brute-force lockout (5 failed logins → 60 s lock).
/// In-memory: a restart invalidates all tokens (intentional).
/// </summary>
public sealed class SettingsTokenService
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DateTime> _tokens = new();
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new();
    private readonly Func<DateTime> _clock;

    private sealed class AttemptState
    {
        public int Fails;
        public DateTime WindowStart;
        public DateTime LockedUntil;
    }

    private readonly int _maxTrackedCallers;

    public SettingsTokenService(Func<DateTime>? clock = null, int maxTrackedCallers = 50_000)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _maxTrackedCallers = Math.Max(16, maxTrackedCallers);
    }

    public (string Token, DateTime ExpiresUtc) Issue(SettingsOptions options)
    {
        Purge();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = _clock().AddHours(Math.Max(1, options.TokenHours));
        _tokens[token] = expires;
        return (token, expires);
    }

    public bool Validate(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        _tokens.TryGetValue(token, out var expires) &&
        (_clock() < expires ? true : Remove(token));

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _tokens.TryRemove(token, out _);
    }

    // ── brute-force lockout ──
    public TimeSpan RemainingLockout(string callerKey)
    {
        if (_attempts.TryGetValue(callerKey, out var s) && s.LockedUntil > _clock())
            return s.LockedUntil - _clock();
        return TimeSpan.Zero;
    }

    /// <summary>Records a failed login. Returns the remaining lockout (Zero = not locked).</summary>
    public TimeSpan RegisterFailure(string callerKey)
    {
        var now = _clock();
        AttemptState s;
        // One gate makes purge + capacity check + eviction + insert atomic, so concurrent
        // writers cannot overshoot the cap or evict an active lockout between check and insert.
        lock (_gate)
        {
            PurgeAttempts(now);
            if (!_attempts.TryGetValue(callerKey, out var existing))
            {
                if (_attempts.Count >= _maxTrackedCallers && !MakeRoom(now))
                    // Table is full of ACTIVE lockouts: fail closed for this untracked caller instead of growing.
                    return Lockout;
                existing = new AttemptState { WindowStart = now };
                _attempts[callerKey] = existing;
            }
            s = existing;
        }
        lock (s)
        {
            if (now - s.WindowStart > FailWindow)
            {
                s.Fails = 0;
                s.WindowStart = now;
            }
            s.Fails++;
            if (s.Fails >= MaxFailures)
            {
                s.LockedUntil = now + Lockout;
                s.Fails = 0;
                s.WindowStart = now;
            }
            return s.LockedUntil > now ? s.LockedUntil - now : TimeSpan.Zero;
        }
    }

    private int _attemptCalls;
    private readonly object _gate = new();

    /// <summary>Bounds the attempt map under hostile many-source traffic: every 256 failures,
    /// drop entries whose failure window expired and that are not locked.</summary>
    private void PurgeAttempts(DateTime now)
    {
        if (Interlocked.Increment(ref _attemptCalls) % 256 != 0) return;
        foreach (var kv in _attempts)
            if (kv.Value.LockedUntil <= now && now - kv.Value.WindowStart > FailWindow)
                _attempts.TryRemove(kv.Key, out _);
    }

    /// <summary>At capacity: drop expired entries, then (batch, amortised) the oldest unlocked ones.
    /// Never evicts an active lockout. Returns false when no room could be made.</summary>
    private bool MakeRoom(DateTime now)
    {
        foreach (var kv in _attempts)
            if (kv.Value.LockedUntil <= now && now - kv.Value.WindowStart > FailWindow)
                _attempts.TryRemove(kv.Key, out _);
        if (_attempts.Count < _maxTrackedCallers) return true;
        var drop = _attempts.Where(kv => kv.Value.LockedUntil <= now)
            .OrderBy(kv => kv.Value.WindowStart).Take(Math.Max(1, _maxTrackedCallers / 10)).Select(kv => kv.Key).ToList();
        foreach (var k in drop) _attempts.TryRemove(k, out _);
        return _attempts.Count < _maxTrackedCallers;
    }

    /// <summary>Entries currently tracked (for tests/diagnostics).</summary>
    public int TrackedCallers => _attempts.Count;

    public void RegisterSuccess(string callerKey) => _attempts.TryRemove(callerKey, out _);

    private bool Remove(string token)
    {
        _tokens.TryRemove(token, out _);
        return false;
    }

    private void Purge()
    {
        var now = _clock();
        foreach (var kv in _tokens)
            if (kv.Value <= now) _tokens.TryRemove(kv.Key, out _);
    }
}
