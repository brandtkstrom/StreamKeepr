using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Twitch;

public sealed class ConduitStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TestRecordStore _records = new();
    private readonly FakeTimeProvider _clock = new(T0);

    public ConduitStateStoreTests() => _records.StartAsync().GetAwaiter().GetResult();

    public void Dispose() => _records.Dispose();

    [Fact]
    public async Task Load_returns_null_when_nothing_was_saved()
    {
        Assert.Null(await CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saved_state_is_one_row_that_survives_a_restart()
    {
        await CreateStore().SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);

        await _records.StartAsync();
        ConduitState? loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(new ConduitState("conduit-1", "client-1"), loaded);
        EventSubConduitRow row = Assert.Single(await ReadRowsAsync());
        Assert.Equal(EventSubConduitRow.SingletonId, row.Id);
        Assert.Equal(T0, row.UpdatedAt);
    }

    [Fact]
    public async Task Saving_again_replaces_the_single_row()
    {
        ConduitStateStore store = CreateStore();
        await store.SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));

        await store.SaveAsync(new ConduitState("conduit-2", "client-2"), CancellationToken.None);

        Assert.Equal(new ConduitState("conduit-2", "client-2"), await CreateStore().LoadAsync(CancellationToken.None));
        EventSubConduitRow row = Assert.Single(await ReadRowsAsync());
        Assert.Equal(T0.AddMinutes(1), row.UpdatedAt);
    }

    [Fact]
    public async Task Clear_removes_the_saved_state()
    {
        ConduitStateStore store = CreateStore();
        await store.SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Empty(await ReadRowsAsync());
    }

    [Fact]
    public async Task Clear_without_saved_state_is_a_no_op()
    {
        await CreateStore().ClearAsync(CancellationToken.None);

        Assert.Empty(await ReadRowsAsync());
    }

    [Theory]
    [InlineData("", "client-1")]
    [InlineData("conduit-1", "")]
    [InlineData("  ", "client-1")]
    public async Task Unreadable_state_loads_as_missing(string conduitId, string clientId)
    {
        await using (RecordStoreDbContext db = await _records.Contexts.CreateDbContextAsync())
        {
            db.EventSubConduits.Add(new EventSubConduitRow { ConduitId = conduitId, ClientId = clientId });
            await db.SaveChangesAsync();
        }

        Assert.Null(await CreateStore().LoadAsync(CancellationToken.None));

        // A later save still replaces it: a new conduit is created and recorded.
        await CreateStore().SaveAsync(new ConduitState("conduit-2", "client-2"), CancellationToken.None);
        Assert.Equal(new ConduitState("conduit-2", "client-2"), await CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_legacy_state_file_is_neither_read_nor_written()
    {
        string appData = AppDataPaths.AppDataDirectory(_records.RecordingsRoot);
        string legacy = Path.Combine(appData, AppDataPaths.EventSubConduitFileName);
        Directory.CreateDirectory(appData);
        await File.WriteAllTextAsync(legacy, "{\"conduit_id\":\"legacy\",\"client_id\":\"client-1\"}");

        ConduitStateStore store = CreateStore();
        Assert.Null(await store.LoadAsync(CancellationToken.None));

        await store.SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);
        await store.ClearAsync(CancellationToken.None);

        Assert.Equal(
            "{\"conduit_id\":\"legacy\",\"client_id\":\"client-1\"}",
            await File.ReadAllTextAsync(legacy));
    }

    [Fact]
    public async Task A_failing_record_store_propagates_instead_of_forgetting_the_conduit()
    {
        await CreateStore().SaveAsync(new ConduitState("conduit-1", "client-1"), CancellationToken.None);
        var failing = new FailableRecordContexts(_records.Contexts, _records.DatabasePath) { Failing = true };
        var store = new ConduitStateStore(failing, _clock, NullLogger<ConduitStateStore>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync(CancellationToken.None));

        failing.Failing = false;
        Assert.Equal(new ConduitState("conduit-1", "client-1"), await store.LoadAsync(CancellationToken.None));
    }

    private ConduitStateStore CreateStore() =>
        new(_records.Contexts, _clock, NullLogger<ConduitStateStore>.Instance);

    private async Task<List<EventSubConduitRow>> ReadRowsAsync()
    {
        await using RecordStoreDbContext db = await _records.Contexts.CreateDbContextAsync();
        return await db.EventSubConduits.AsNoTracking().ToListAsync();
    }
}
