using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class ConduitStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Load_returns_null_when_nothing_was_saved()
    {
        Assert.Null(await CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saved_state_survives_a_new_store_instance()
    {
        await CreateStore().SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);

        ConduitState? loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(new ConduitState("conduit-1", "client-1"), loaded);
        Assert.True(File.Exists(AppDataPaths.EventSubConduitFile(_root)));
    }

    [Fact]
    public async Task Saved_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        await CreateStore().SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(AppDataPaths.EventSubConduitFile(_root)));
    }

    [Fact]
    public async Task Clear_removes_the_saved_state()
    {
        ConduitStateStore store = CreateStore();
        await store.SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.False(File.Exists(AppDataPaths.EventSubConduitFile(_root)));
    }

    [Fact]
    public async Task Clear_without_a_file_is_a_no_op()
    {
        await CreateStore().ClearAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"conduit_id\":\"\",\"client_id\":\"client-1\"}")]
    [InlineData("null")]
    public async Task Unreadable_state_loads_as_missing(string contents)
    {
        string path = AppDataPaths.EventSubConduitFile(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);

        Assert.Null(await CreateStore().LoadAsync(CancellationToken.None));
    }

    private ConduitStateStore CreateStore() =>
        new(
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            NullLogger<ConduitStateStore>.Instance);
}
