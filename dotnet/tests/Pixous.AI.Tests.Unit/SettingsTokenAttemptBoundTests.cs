using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Unit;

public class SettingsTokenAttemptBoundTests
{
    [Fact]
    public void ExpiredFailureWindows_ArePurged_SoMapStaysBounded()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var s = new SettingsTokenService(() => now);
        for (var i = 0; i < 2000; i++) s.RegisterFailure("src-" + i);
        Assert.True(s.TrackedCallers >= 1900); // nothing expired yet
        now = now.AddMinutes(16);              // past the 15 min failure window
        for (var i = 0; i < 512; i++) s.RegisterFailure("later-" + i);
        Assert.True(s.TrackedCallers < 700, $"tracked={s.TrackedCallers}");
    }

    [Fact]
    public void ActiveLockout_IsNotPurged()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var s = new SettingsTokenService(() => now);
        for (var i = 0; i < 5; i++) s.RegisterFailure("victim");
        Assert.True(s.RemainingLockout("victim") > TimeSpan.Zero);
        for (var i = 0; i < 600; i++) s.RegisterFailure("noise-" + i);
        Assert.True(s.RemainingLockout("victim") > TimeSpan.Zero);
    }
}

public class SettingsTokenHardCapTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ManyDistinctActiveKeys_StayUnderCap()
    {
        var s = new SettingsTokenService(() => T0, maxTrackedCallers: 1000);
        for (var i = 0; i < 50_000; i++) s.RegisterFailure("k" + i);
        Assert.True(s.TrackedCallers <= 1000, $"tracked={s.TrackedCallers}");
    }

    [Fact]
    public void ActiveLockout_SurvivesCapPressure()
    {
        var s = new SettingsTokenService(() => T0, maxTrackedCallers: 1000);
        for (var i = 0; i < 5; i++) s.RegisterFailure("victim");
        for (var i = 0; i < 20_000; i++) s.RegisterFailure("noise" + i);
        Assert.True(s.RemainingLockout("victim") > TimeSpan.Zero);
        Assert.True(s.TrackedCallers <= 1000);
    }

    [Fact]
    public void TableFullOfLockouts_FailsClosedForNewCaller_WithoutGrowing()
    {
        var s = new SettingsTokenService(() => T0, maxTrackedCallers: 16);
        for (var k = 0; k < 16; k++) for (var i = 0; i < 5; i++) s.RegisterFailure("locked" + k);
        var r = s.RegisterFailure("newcomer");
        Assert.True(r > TimeSpan.Zero);
        Assert.True(s.TrackedCallers <= 16);
    }
}

public class SettingsTokenConcurrencyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConcurrentWriters_AtCap_NeverExceedCap_AndKeepActiveLockouts()
    {
        const int cap = 500;
        var s = new SettingsTokenService(() => T0, maxTrackedCallers: cap);
        for (var v = 0; v < 50; v++) for (var i = 0; i < 5; i++) s.RegisterFailure("victim" + v);
        var peak = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                s.RegisterFailure($"t{t}-k{i}");
                var c = s.TrackedCallers;
                int p; while (c > (p = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, c, p);
            }
        });
        Assert.True(peak <= cap, $"peak tracked={peak} cap={cap}");
        for (var v = 0; v < 50; v++) Assert.True(s.RemainingLockout("victim" + v) > TimeSpan.Zero, "victim" + v);
    }
}
