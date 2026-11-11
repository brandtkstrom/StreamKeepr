using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class ChannelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

    public ChannelStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Load_seeds_normalizes_dedupes_and_does_not_reseed()
    {
        ChannelStore store = CreateStore(" Streamer ", "STREAMER", "Other");

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(["streamer", "other"], store.Snapshot.Select(entry => entry.Login));
        List<ChannelEntry> persisted = ReadFile();
        Assert.Equal(["streamer", "other"], persisted.Select(entry => entry.Login));

        ChannelStore second = CreateStore("different");
        await second.LoadAsync(CancellationToken.None);

        Assert.Equal(["streamer", "other"], second.Snapshot.Select(entry => entry.Login));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[{\"login\":\"BAD!\"}]")]
    [InlineData("[{\"login\":\"dup\"},{\"login\":\"DUP\"}]")]
    [InlineData("[{\"login\":\"ok\",\"userId\":\"not-a-number\"}]")]
    public async Task Corrupt_file_fails_load_and_is_left_untouched(string content)
    {
        Directory.CreateDirectory(AppDataPaths.AppDataDirectory(_root));
        string path = AppDataPaths.ChannelsFile(_root);
        await File.WriteAllTextAsync(path, content);

        ChannelStore store = CreateStore("streamer");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync(CancellationToken.None));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_normalizes_logins_from_an_existing_file()
    {
        Directory.CreateDirectory(AppDataPaths.AppDataDirectory(_root));
        await File.WriteAllTextAsync(
            AppDataPaths.ChannelsFile(_root),
            "[{\"login\":\"Streamer\",\"userId\":\"123\"}]");

        ChannelStore store = CreateStore();
        await store.LoadAsync(CancellationToken.None);

        ChannelEntry entry = Assert.Single(store.Snapshot);
        Assert.Equal("streamer", entry.Login);
        Assert.Equal("123", entry.UserId);
    }

    [Fact]
    public async Task Invalid_seed_fails_and_does_not_create_the_file()
    {
        ChannelStore store = CreateStore("BAD!");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync(CancellationToken.None));

        Assert.False(File.Exists(AppDataPaths.ChannelsFile(_root)));
    }

    [Fact]
    public async Task Modifying_before_load_throws_and_does_not_create_the_file()
    {
        ChannelStore store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddAsync("streamer", CancellationToken.None));

        Assert.False(File.Exists(AppDataPaths.ChannelsFile(_root)));
    }

    [Fact]
    public async Task Add_remove_pause_and_profile_raise_the_right_events()
    {
        ChannelStore store = CreateStore();
        await store.LoadAsync(CancellationToken.None);

        int changed = 0;
        int subscriptionsChanged = 0;
        store.Changed += () => changed++;
        store.SubscriptionsChanged += () => subscriptionsChanged++;

        ChannelEntry added = await store.AddAsync(" New_Login ", CancellationToken.None);

        Assert.Equal("new_login", added.Login);
        Assert.Equal(1, changed);
        Assert.Equal(1, subscriptionsChanged);

        await store.AddAsync("new_login", CancellationToken.None);
        Assert.Equal(1, changed);
        Assert.Equal(1, subscriptionsChanged);

        await store.SetPausedAsync("new_login", paused: true, CancellationToken.None);
        Assert.Equal(2, changed);
        Assert.Equal(1, subscriptionsChanged);

        await store.SetProfileAsync(
            "new_login", "123", "Display", avatarUrl: null, CancellationToken.None);
        Assert.Equal(3, changed);
        Assert.Equal(2, subscriptionsChanged);

        await store.SetProfileAsync(
            "new_login", "123", "Renamed", avatarUrl: "https://example/avatar.png",
            CancellationToken.None);
        Assert.Equal(4, changed);
        Assert.Equal(2, subscriptionsChanged);

        await store.SetProfileAsync(
            "new_login", "456", "Renamed", avatarUrl: null, CancellationToken.None);
        Assert.Equal(5, changed);
        Assert.Equal(3, subscriptionsChanged);

        await store.RemoveAsync("new_login", CancellationToken.None);
        Assert.Equal(6, changed);
        Assert.Equal(4, subscriptionsChanged);
        Assert.Empty(store.Snapshot);
        Assert.Empty(ReadFile());
    }

    [Fact]
    public async Task Paused_and_profile_fields_round_trip_through_the_file()
    {
        ChannelStore store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        await store.AddAsync("streamer", CancellationToken.None);
        await store.SetPausedAsync("streamer", paused: true, CancellationToken.None);
        await store.SetProfileAsync(
            "streamer",
            "123456",
            "Streamer",
            "https://example/avatar.png",
            CancellationToken.None);

        ChannelStore second = CreateStore("other");
        await second.LoadAsync(CancellationToken.None);

        ChannelEntry entry = Assert.Single(second.Snapshot);
        Assert.Equal("streamer", entry.Login);
        Assert.True(entry.Paused);
        Assert.Equal("123456", entry.UserId);
        Assert.Equal("Streamer", entry.DisplayName);
        Assert.Equal("https://example/avatar.png", entry.AvatarUrl);
    }

    [Fact]
    public async Task Concurrent_adds_are_serialized_and_leave_one_atomic_file()
    {
        ChannelStore store = CreateStore();
        await store.LoadAsync(CancellationToken.None);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 25),
            async (index, ct) => await store.AddAsync($"channel_{index}", ct));

        Assert.Equal(25, store.Snapshot.Count);
        Assert.Equal(25, ReadFile().Count);
        Assert.False(File.Exists(AppDataPaths.ChannelsFile(_root) + ".tmp"));
    }

    [Fact]
    public async Task Invalid_profile_user_id_is_rejected()
    {
        ChannelStore store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        await store.AddAsync("streamer", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetProfileAsync(
            "streamer", "not-a-number", "Streamer", null, CancellationToken.None));
    }

    private ChannelStore CreateStore(params string[] seeds) =>
        new(
            Options.Create(new TwitchOptions { Channels = [.. seeds] }),
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            NullLogger<ChannelStore>.Instance);

    private List<ChannelEntry> ReadFile() =>
        JsonSerializer.Deserialize<List<ChannelEntry>>(
            File.ReadAllText(AppDataPaths.ChannelsFile(_root)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("The channel test file is null.");
}
