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

    public SettingsTokenService(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

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
        var s = _attempts.GetOrAdd(callerKey, _ => new AttemptState { WindowStart = now });
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
