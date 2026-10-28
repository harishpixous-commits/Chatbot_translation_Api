using Pixous.AI.Core.Services;
using Xunit;

namespace Pixous.AI.Tests.Unit;

/// <summary>Project service-key store: hashing, lifecycle, rate windows, persistence, file perms.</summary>
public class ServiceKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pixous-sk-" + Guid.NewGuid().ToString("n"));
    public ServiceKeyStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Create_Validates_List_Revoke_Rotate()
    {
        var store = new ServiceKeyStore(_dir);
        var (raw, info) = store.Create("billing-api");

        Assert.StartsWith("pk_", raw);
        Assert.Equal("billing-api", info.Name);
        Assert.Equal(raw[..10], info.Prefix);

        Assert.NotNull(store.Validate(raw));
        Assert.Single(store.List());

        // Rotate: old dies, new works, id preserved
        var rotated = store.Rotate(info.Id);
        Assert.NotNull(rotated);
        Assert.Null(store.Validate(raw));
        Assert.NotNull(store.Validate(rotated!.Value.RawKey));
        Assert.Equal(info.Id, rotated.Value.Info.Id);

        // Revoke: rejected; unknown key: rejected
        Assert.True(store.Revoke(info.Id));
        Assert.Null(store.Validate(rotated.Value.RawKey));
        Assert.Null(store.Validate("pk_nonsense"));
        Assert.Null(store.Validate(null));
        Assert.False(store.Revoke("no-such-id"));
        Assert.Null(store.Rotate("no-such-id"));
    }

    [Fact]
    public void Raw_Key_Is_Never_Persisted_Only_Hash()
    {
        var store = new ServiceKeyStore(_dir);
        var (raw, _) = store.Create("web-frontend");
        var file = File.ReadAllText(Path.Combine(_dir, "service-keys.json"));
        Assert.DoesNotContain(raw, file);
        Assert.Contains(raw[..10], file); // prefix only
    }

    [Fact]
    public void Persists_Across_Store_Instances()
    {
        var store = new ServiceKeyStore(_dir);
        var (raw, info) = store.Create("mobile-backend");
        store.RecordUsage(info.Id);

        var reopened = new ServiceKeyStore(_dir);
        Assert.NotNull(reopened.Validate(raw));
        var listed = Assert.Single(reopened.List());
        Assert.Equal(1, listed.TotalRequests);
        Assert.NotNull(listed.LastUsedUtc);
    }

    [Fact]
    public void Per_Minute_Permit_Window_Closes_And_Reopens()
    {
        var now = DateTime.UtcNow;
        var store = new ServiceKeyStore(_dir, clock: () => now);
        var (raw, info) = store.Create("noisy");

        for (var i = 0; i < 5; i++)
            Assert.True(store.TryAcquirePermit(info.Id, perMinuteLimit: 5));
        Assert.False(store.TryAcquirePermit(info.Id, perMinuteLimit: 5));

        now = now.AddMinutes(1).AddSeconds(1); // window rolls
        Assert.True(store.TryAcquirePermit(info.Id, perMinuteLimit: 5));

        // Other keys have their own window
        var (_, other) = store.Create("quiet");
        Assert.True(store.TryAcquirePermit(other.Id, perMinuteLimit: 5));
    }

    [Fact]
    public void Key_File_Is_Owner_Only_On_Unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new ServiceKeyStore(_dir);
        store.Create("perms");
        var mode = File.GetUnixFileMode(Path.Combine(_dir, "service-keys.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }
}
