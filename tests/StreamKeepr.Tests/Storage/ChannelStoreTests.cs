using Microsoft.Data.Sqlite;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Storage;

public sealed class ChannelStoreTests : IDisposable
{
    private readonly TestRecordStore _records = new();

    public void Dispose() => _records.Dispose();

    [Fact]
    public async Task First_start_seeds_normalized_deduped_channels_and_a_restart_does_not_reseed()
    {
        ChannelStore store = await _records.LoadChannelStoreAsync(" Streamer ", "STREAMER", "Other");

        Assert.Equal(["streamer", "other"], store.Snapshot.Select(entry => entry.Login));

        ChannelStore restarted = await _records.LoadChannelStoreAsync("different");

        Assert.Equal(["streamer", "other"], restarted.Snapshot.Select(entry => entry.Login));
    }

    [Fact]
    public async Task Removing_every_channel_stays_empty_across_a_restart()
    {
        ChannelStore store = await _records.LoadChannelStoreAsync("streamer", "other");
        await store.RemoveAsync("streamer", CancellationToken.None);
        await store.RemoveAsync("other", CancellationToken.None);

        ChannelStore restarted = await _records.LoadChannelStoreAsync("streamer", "other");

        Assert.Empty(restarted.Snapshot);
    }

    [Fact]
    public async Task A_first_start_that_stopped_before_loading_channels_still_seeds_on_the_next_start()
    {
        // The first start migrated the record store, then a later startup step failed.
        await _records.StartAsync();

        ChannelStore restarted = await _records.LoadChannelStoreAsync("streamer");

        Assert.Equal(["streamer"], restarted.Snapshot.Select(entry => entry.Login));
    }

    [Fact]
    public async Task Loading_twice_in_one_start_does_not_reseed_an_emptied_list()
    {
        ChannelStore store = _records.CreateChannelStore(await _records.StartAsync(), "streamer");
        await store.LoadAsync(CancellationToken.None);
        await store.RemoveAsync("streamer", CancellationToken.None);

        await store.LoadAsync(CancellationToken.None);

        Assert.Empty(store.Snapshot);
    }

    [Fact]
    public async Task Invalid_seed_fails_and_stores_nothing()
    {
        ChannelStore store = _records.CreateChannelStore(await _records.StartAsync(), "BAD!");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));

        ChannelStore restarted = await _records.LoadChannelStoreAsync();
        Assert.Empty(restarted.Snapshot);
    }

    [Theory]
    [InlineData("BAD!", null)]
    [InlineData("Streamer", null)]
    [InlineData("ok", "not-a-number")]
    public async Task An_invalid_stored_row_fails_load(string login, string? userId)
    {
        await _records.StartAsync();
        await using (var connection = new SqliteConnection($"Data Source={_records.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO Channels (Login, Position, Paused, UserId) VALUES ($login, 0, 0, $userId);";
            insert.Parameters.AddWithValue("$login", login);
            insert.Parameters.AddWithValue("$userId", (object?)userId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        ChannelStore store = _records.CreateChannelStore(await _records.StartAsync());

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Loading_before_the_record_store_is_prepared_throws()
    {
        ChannelStore store = _records.CreateChannelStore(new RecordStoreState(), "streamer");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Modifying_before_load_throws_and_stores_nothing()
    {
        ChannelStore store = _records.CreateChannelStore(await _records.StartAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddAsync("streamer", CancellationToken.None));

        ChannelStore restarted = await _records.LoadChannelStoreAsync();
        Assert.Empty(restarted.Snapshot);
    }

    [Fact]
    public async Task A_legacy_channels_file_is_neither_read_nor_written()
    {
        Directory.CreateDirectory(AppDataPaths.AppDataDirectory(_records.RecordingsRoot));
        string legacy = AppDataPaths.ChannelsFile(_records.RecordingsRoot);
        await File.WriteAllTextAsync(legacy, "[{\"login\":\"legacy\"}]");

        ChannelStore store = await _records.LoadChannelStoreAsync("seeded");
        await store.AddAsync("added", CancellationToken.None);

        Assert.Equal(["seeded", "added"], store.Snapshot.Select(entry => entry.Login));
        Assert.Equal("[{\"login\":\"legacy\"}]", await File.ReadAllTextAsync(legacy));
    }

    [Fact]
    public async Task Add_remove_pause_and_profile_raise_the_right_events()
    {
        ChannelStore store = await _records.LoadChannelStoreAsync();

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
    }

    [Fact]
    public async Task Add_remove_pause_and_profile_changes_survive_a_restart()
    {
        ChannelStore store = await _records.LoadChannelStoreAsync("seeded", "removed");
        await store.AddAsync("streamer", CancellationToken.None);
        await store.RemoveAsync("removed", CancellationToken.None);
        await store.SetPausedAsync("streamer", paused: true, CancellationToken.None);
        await store.SetProfileAsync(
            "streamer",
            "123456",
            "Streamer",
            "https://example/avatar.png",
            CancellationToken.None);

        ChannelStore restarted = await _records.LoadChannelStoreAsync("other");

        Assert.Equal(
            [
                new ChannelEntry("seeded"),
                new ChannelEntry("streamer", Paused: true, "123456", "Streamer", "https://example/avatar.png"),
            ],
            restarted.Snapshot);
    }

    [Fact]
    public async Task Concurrent_adds_are_serialized_and_all_persist()
    {
        ChannelStore store = await _records.LoadChannelStoreAsync();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 25),
            async (index, ct) => await store.AddAsync($"channel_{index}", ct));

        Assert.Equal(25, store.Snapshot.Count);
        ChannelStore restarted = await _records.LoadChannelStoreAsync();
        Assert.Equal(store.Snapshot, restarted.Snapshot);
    }

    [Fact]
    public async Task Invalid_profile_user_id_is_rejected()
    {
        ChannelStore store = await _records.LoadChannelStoreAsync();
        await store.AddAsync("streamer", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetProfileAsync(
            "streamer", "not-a-number", "Streamer", null, CancellationToken.None));
    }
}
