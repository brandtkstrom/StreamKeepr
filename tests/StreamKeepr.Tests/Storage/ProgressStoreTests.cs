using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class ProgressStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly RecordingLibrary _library = new(new FakeTimeProvider(), NullLogger<RecordingLibrary>.Instance);

    private string FilePath => AppDataPaths.ProgressFile(_root);

    [Fact]
    public async Task Set_is_readable_at_once_and_clamped_to_the_duration()
    {
        _library.Upsert(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();
        int changes = 0;
        store.Changed += () => changes++;

        await store.SetAsync("r1", 125.5, CancellationToken.None);
        Assert.Equal(125.5, store.Get("r1"));

        await store.SetAsync("r1", -3, CancellationToken.None);
        Assert.Equal(0, store.Get("r1"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Positions_within_thirty_seconds_of_the_end_clear_the_entry()
    {
        _library.Upsert(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("r1", 100, CancellationToken.None);

        await store.SetAsync("r1", 571, CancellationToken.None);
        Assert.Null(store.Get("r1"));

        await store.SetAsync("r1", 900, CancellationToken.None);
        Assert.Null(store.Get("r1"));
    }

    [Fact]
    public async Task Unknown_recordings_are_ignored()
    {
        using ProgressStore store = await CreateAsync();
        int changes = 0;
        store.Changed += () => changes++;

        await store.SetAsync("missing", 10, CancellationToken.None);

        Assert.Null(store.Get("missing"));
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Live_recordings_keep_positions_near_and_past_the_known_end()
    {
        // While recording the live edge sits inside the 30 s margin, and the library's
        // duration trails the player's, so neither the margin nor the upper clamp applies.
        _library.Upsert(Finished("live", durationSeconds: 600, recording: true));
        using ProgressStore store = await CreateAsync();

        await store.SetAsync("live", 590, CancellationToken.None);
        Assert.Equal(590, store.Get("live"));

        await store.SetAsync("live", 612, CancellationToken.None);
        Assert.Equal(612, store.Get("live"));

        await store.SetAsync("live", -3, CancellationToken.None);
        Assert.Equal(0, store.Get("live"));
    }

    [Fact]
    public async Task Live_recordings_without_a_known_duration_keep_positions()
    {
        _library.Upsert(Finished("live", durationSeconds: null, recording: true));
        using ProgressStore store = await CreateAsync();

        await store.SetAsync("live", 10, CancellationToken.None);

        Assert.Equal(10, store.Get("live"));
    }

    [Fact]
    public async Task A_capture_finishing_drops_positions_within_thirty_seconds_of_its_final_end()
    {
        _library.Upsert(Finished("near", durationSeconds: 500, recording: true));
        _library.Upsert(Finished("edge", durationSeconds: 500, recording: true));
        _library.Upsert(Finished("kept", durationSeconds: 500, recording: true));
        _library.MarkLoaded();
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("near", 590, CancellationToken.None);
        await store.SetAsync("edge", 570, CancellationToken.None);
        await store.SetAsync("kept", 569.5, CancellationToken.None);
        int changes = 0;
        store.Changed += () => changes++;

        _library.Upsert(Finished("near", durationSeconds: 600));
        _library.Upsert(Finished("edge", durationSeconds: 600));
        _library.Upsert(Finished("kept", durationSeconds: 600));

        Assert.Null(store.Get("near"));
        Assert.Null(store.Get("edge"));
        Assert.Equal(569.5, store.Get("kept"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_capture_still_recording_keeps_positions_near_its_current_end()
    {
        _library.Upsert(Finished("live", durationSeconds: 600, recording: true));
        _library.MarkLoaded();
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("live", 590, CancellationToken.None);

        _library.Upsert(Finished("live", durationSeconds: 610, recording: true));

        Assert.Equal(590, store.Get("live"));
    }

    [Fact]
    public async Task A_stored_position_near_the_end_of_a_session_recovered_as_finished_is_dropped_at_startup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, """{"near":590,"kept":100}""");
        _library.Upsert(Finished("near", durationSeconds: 600));
        _library.Upsert(Finished("kept", durationSeconds: 600));
        _library.MarkLoaded();

        using ProgressStore store = await CreateAsync();

        Assert.Null(store.Get("near"));
        Assert.Equal(100, store.Get("kept"));
    }

    [Fact]
    public async Task Writes_are_batched_to_at_most_one_every_ten_seconds()
    {
        _library.Upsert(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();

        await store.SetAsync("r1", 10, CancellationToken.None);
        Assert.False(File.Exists(FilePath));

        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitForFileAsync(positions => positions.GetValueOrDefault("r1") == 10);

        await store.SetAsync("r1", 20, CancellationToken.None);
        await store.SetAsync("r1", 30, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(10, ReadFile()["r1"]);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForFileAsync(positions => positions.GetValueOrDefault("r1") == 30);
    }

    [Fact]
    public async Task Flush_writes_pending_changes_and_a_new_store_loads_them()
    {
        _library.Upsert(Finished("r1", durationSeconds: 600));
        using (ProgressStore store = await CreateAsync())
        {
            await store.SetAsync("r1", 42, CancellationToken.None);
            await store.FlushAsync(CancellationToken.None);
        }

        Assert.False(File.Exists(FilePath + ".tmp"));
        using ProgressStore reloaded = await CreateAsync();
        Assert.Equal(42, reloaded.Get("r1"));
    }

    [Fact]
    public async Task A_corrupt_file_starts_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, "{ not json");

        using ProgressStore store = await CreateAsync();

        Assert.Null(store.Get("r1"));
    }

    [Fact]
    public async Task Clear_and_library_removal_drop_the_entry()
    {
        _library.Upsert(Finished("r1", durationSeconds: 600));
        _library.Upsert(Finished("r2", durationSeconds: 600));
        _library.MarkLoaded();
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("r1", 50, CancellationToken.None);
        await store.SetAsync("r2", 60, CancellationToken.None);

        await store.ClearAsync("r1", CancellationToken.None);
        _library.Remove("r2");

        Assert.Null(store.Get("r1"));
        Assert.Null(store.Get("r2"));
        await store.FlushAsync(CancellationToken.None);
        Assert.Empty(ReadFile());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<ProgressStore> CreateAsync()
    {
        var store = new ProgressStore(
            _library,
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            _clock,
            NullLogger<ProgressStore>.Instance);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private Dictionary<string, double> ReadFile() =>
        JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath))!;

    private async Task WaitForFileAsync(Func<Dictionary<string, double>, bool> condition)
    {
        for (int attempt = 0; attempt < 2_000; attempt++)
        {
            try
            {
                if (File.Exists(FilePath) && condition(ReadFile()))
                    return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }

            await Task.Delay(5);
        }

        Assert.Fail("The progress file never reached the expected state.");
    }

    private static RecordingSummary Finished(
        string id, double? durationSeconds, bool recording = false) =>
        new(
            id,
            "ann",
            "Title",
            "Game",
            Now.AddHours(-2),
            recording ? null : Now.AddHours(-1),
            IsRecording: recording,
            durationSeconds,
            SizeBytes: 1024,
            PartCount: 1,
            SegmentCount: 10,
            HasThumbnail: false,
            Chapters: []);
}
