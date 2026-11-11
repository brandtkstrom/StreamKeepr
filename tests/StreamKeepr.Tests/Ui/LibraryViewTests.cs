using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class LibraryViewTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Starts_loaded_state_from_the_library_and_finds_nothing()
    {
        using Harness harness = await Harness.CreateAsync("alpha");

        Assert.False(harness.View.IsLoaded);
        Assert.Empty(harness.View.Streamers(new LibraryQuery()));
        Assert.Null(harness.View.Find("missing"));

        harness.Library.MarkLoaded();

        Assert.True(harness.View.IsLoaded);
    }

    [Fact]
    public async Task Derives_recording_and_reconnecting_from_the_active_job()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        RecordingSummary summary = Summary(
            "rec-1", "alpha", Now.AddMinutes(-5), recording: true);
        harness.Library.Upsert(summary);

        RecordingView reconnecting = Assert.IsType<RecordingView>(
            harness.View.Find("rec-1"));
        Assert.Equal(RecordingState.Reconnecting, reconnecting.State);
        Assert.Null(reconnecting.EndedAt);
        Assert.Null(reconnecting.SizeBytes);

        harness.MarkActive(summary);

        RecordingView recording = Assert.IsType<RecordingView>(
            harness.View.Find("rec-1"));
        Assert.Equal(RecordingState.Recording, recording.State);
        Assert.Equal(300, recording.DurationSec, precision: 3);
    }

    [Fact]
    public async Task Derives_partial_and_saved_with_complete_session_values()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        harness.Add(Summary("saved", "alpha", Now.AddHours(-2), parts: 1));
        harness.Add(Summary("partial", "alpha", Now.AddHours(-1), parts: 2));

        RecordingView saved = Assert.IsType<RecordingView>(harness.View.Find("saved"));
        Assert.Equal(RecordingState.Saved, saved.State);
        Assert.Equal(3600, saved.DurationSec);
        Assert.Equal(4096, saved.SizeBytes);
        Assert.Equal("Game", saved.Category);
        Assert.Equal("/media/alpha/saved/index.m3u8", saved.PlaylistUrl);

        RecordingView partial = Assert.IsType<RecordingView>(harness.View.Find("partial"));
        Assert.Equal(RecordingState.Partial, partial.State);
        Assert.Equal(2, partial.PartCount);
    }

    [Fact]
    public async Task Streamers_put_recording_first_then_newest_with_login_ties()
    {
        using Harness harness = await Harness.CreateAsync("alpha", "beta", "gamma", "delta");
        harness.Add(Summary("a1", "alpha", Now.AddHours(-8)));
        harness.Add(Summary("b1", "beta", Now.AddHours(-2)));
        harness.Add(Summary("d1", "delta", Now.AddHours(-2)));
        RecordingSummary recording = Summary(
            "g1", "gamma", Now.AddHours(-9), recording: true);
        harness.Add(recording);
        harness.MarkActive(recording);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        IReadOnlyList<StreamerView> streamers =
            harness.View.Streamers(new LibraryQuery());

        Assert.Equal(["gamma", "beta", "delta", "alpha"], streamers.Select(item => item.Login));
        Assert.True(streamers[0].IsRecording);
        Assert.Equal(Now.AddHours(-9), streamers[0].LastRecordedAt);
        Assert.Equal(1, streamers[0].RecordingCount);
    }

    [Fact]
    public async Task Streamers_support_oldest_and_az_but_keep_recording_first()
    {
        using Harness harness = await Harness.CreateAsync("alpha", "beta", "gamma");
        await harness.SetProfileAsync("alpha", "Zeta");
        await harness.SetProfileAsync("beta", "Alpha");
        await harness.SetProfileAsync("gamma", "Mike");
        harness.Add(Summary("a1", "alpha", Now.AddHours(-8)));
        harness.Add(Summary("b1", "beta", Now.AddHours(-2)));
        RecordingSummary recording = Summary(
            "g1", "gamma", Now.AddHours(-9), recording: true);
        harness.Add(recording);
        harness.MarkActive(recording);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        IReadOnlyList<StreamerView> oldest =
            harness.View.Streamers(new LibraryQuery(Sort: "oldest"));
        Assert.Equal(["gamma", "alpha", "beta"], oldest.Select(item => item.Login));

        IReadOnlyList<StreamerView> az =
            harness.View.Streamers(new LibraryQuery(Sort: "az"));
        Assert.Equal(["gamma", "beta", "alpha"], az.Select(item => item.Login));
    }

    [Fact]
    public async Task Recordings_default_to_newest_and_break_ties_by_id()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        DateTimeOffset same = Now.AddHours(-3);
        harness.Add(Summary("b", "alpha", same));
        harness.Add(Summary("a", "alpha", same));
        harness.Add(Summary("c", "alpha", Now.AddHours(-1)));

        Assert.Equal(
            ["c", "a", "b"],
            harness.View.Recordings("alpha", new LibraryQuery()).Select(item => item.Id));

        Assert.Equal(
            ["a", "b", "c"],
            harness.View.Recordings("alpha", new LibraryQuery(Sort: "oldest"))
                .Select(item => item.Id));

        Assert.Equal(
            ["c", "a", "b"],
            harness.View.Recordings("alpha", new LibraryQuery(Sort: "az"))
                .Select(item => item.Id));
    }

    [Fact]
    public async Task Search_and_state_filters_follow_the_contract()
    {
        using Harness harness = await Harness.CreateAsync("alpha", "beta");
        await harness.SetProfileAsync("alpha", "Chef Ann");
        harness.Add(Summary(
            "rec", "alpha", Now.AddHours(-4), recording: true, title: "Cooking night",
            game: "Food"));
        harness.Add(Summary(
            "partial", "beta", Now.AddHours(-2), parts: 2, title: "Speedrun", game: "Celeste"));
        harness.Add(Summary(
            "saved", "beta", Now.AddHours(-1), parts: 1, title: "Art stream", game: "Art"));

        Assert.Equal(
            ["rec"],
            harness.View.Recordings("alpha", new LibraryQuery(Search: " chef "))
                .Select(item => item.Id));
        Assert.Equal(
            ["rec"],
            harness.View.Recordings("alpha", new LibraryQuery(Search: "food"))
                .Select(item => item.Id));
        Assert.Equal(
            ["rec"],
            harness.View.Recordings("alpha", new LibraryQuery(Search: "cook"))
                .Select(item => item.Id));

        Assert.Equal(
            ["saved", "partial"],
            harness.View.Recordings("beta", new LibraryQuery(State: "saved"))
                .Select(item => item.Id));
        Assert.Equal(
            ["rec"],
            harness.View.Recordings("alpha", new LibraryQuery(State: "recording"))
                .Select(item => item.Id));
        Assert.Empty(harness.View.Recordings("beta", new LibraryQuery(State: "reconnecting")));
        Assert.Equal(
            2,
            harness.View.Streamers(new LibraryQuery()).Count);

        Assert.Single(harness.View.Streamers(new LibraryQuery(Search: "chef")));
        Assert.Empty(harness.View.Streamers(new LibraryQuery(Search: "nothing")));
    }

    [Fact]
    public async Task Invalid_filter_sort_and_empty_search_fall_back()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        harness.Add(Summary("old", "alpha", Now.AddHours(-2)));
        harness.Add(Summary("new", "alpha", Now.AddHours(-1)));

        var invalid = new LibraryQuery(State: "nope", Search: "   ", Sort: "sideways");

        Assert.Equal(
            ["new", "old"],
            harness.View.Recordings("alpha", invalid).Select(item => item.Id));
        Assert.Single(harness.View.Streamers(invalid));
    }

    [Fact]
    public async Task Changed_is_throttled_to_one_event_per_window_and_stops_on_dispose()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        int changed = 0;
        harness.View.Changed += () => changed++;

        harness.Library.Upsert(Summary("one", "alpha", Now.AddHours(-2)));
        harness.Library.Upsert(Summary("two", "alpha", Now.AddHours(-1)));

        Assert.Equal(0, changed);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(0, changed);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, changed);

        harness.Library.Upsert(Summary("three", "alpha", Now.AddHours(-3)));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, changed);

        harness.View.Dispose();
        harness.Library.Upsert(Summary("four", "alpha", Now.AddHours(-4)));
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task Changed_is_not_raised_when_the_visible_view_is_equivalent()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        harness.Add(Summary("one", "alpha", Now.AddHours(-2)));
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        int changed = 0;
        harness.View.Changed += () => changed++;

        // A new summary object with the same projected values is a no-op for the UI.
        harness.Library.Upsert(Summary("one", "alpha", Now.AddHours(-2)));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Reuses_view_model_instances_for_equivalent_updates()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        harness.Add(Summary("one", "alpha", Now.AddHours(-2)));
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        RecordingView first = Assert.IsType<RecordingView>(harness.View.Find("one"));

        harness.Add(Summary("one", "alpha", Now.AddHours(-2)));
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        RecordingView second = Assert.IsType<RecordingView>(harness.View.Find("one"));
        RecordingView listed = Assert.Single(harness.View.Recordings("alpha", new LibraryQuery()));

        Assert.Same(first, second);
        Assert.Same(first, listed);
    }

    [Fact]
    public async Task Storage_changes_are_coalesced_and_exposed()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        int changed = 0;
        harness.View.Changed += () => changed++;

        harness.Disk.FreeBytes = harness.Disk.TotalBytes * 40 / 100;
        harness.Storage.Refresh();

        Assert.Equal(0, changed);
        Assert.Equal(40, harness.View.Storage.FreeBytes * 100 / harness.View.Storage.TotalBytes);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Streamer_metadata_comes_from_the_channel_store()
    {
        using Harness harness = await Harness.CreateAsync("alpha");
        await harness.SetProfileAsync("alpha", "Alpha One", "https://example.test/a.png");
        await harness.SetPausedAsync("alpha", true);
        harness.Add(Summary("one", "alpha", Now.AddHours(-2)));

        StreamerView streamer = Assert.Single(harness.View.Streamers(new LibraryQuery()));

        Assert.Equal("Alpha One", streamer.DisplayName);
        Assert.Equal("https://example.test/a.png", streamer.AvatarUrl);
        Assert.True(streamer.IsWatched);
        Assert.True(streamer.IsPaused);
        Assert.False(streamer.IsRecording);
    }

    [Fact]
    public async Task Unwatched_streamers_still_appear_with_login_fallback()
    {
        using Harness harness = await Harness.CreateAsync();
        harness.Add(Summary("one", "orphan", Now.AddHours(-2)));

        StreamerView streamer = Assert.Single(harness.View.Streamers(new LibraryQuery()));

        Assert.Equal("orphan", streamer.DisplayName);
        Assert.False(streamer.IsWatched);
    }

    private static RecordingSummary Summary(
        string id,
        string channel,
        DateTimeOffset startedAt,
        bool recording = false,
        int parts = 1,
        string title = "Recording",
        string game = "Game")
    {
        return new RecordingSummary(
            id,
            channel,
            title,
            game,
            startedAt,
            recording ? null : startedAt.AddHours(1),
            recording,
            recording ? null : 3600,
            recording ? null : 4096,
            parts,
            12,
            HasThumbnail: false,
            Chapters: []);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        private Harness(
            string root,
            FakeTimeProvider clock,
            RecordingLibrary library,
            ChannelStore channels,
            DvrStatus status,
            MutableDiskSpace disk,
            StorageMonitor storage,
            LibraryView view)
        {
            _root = root;
            Clock = clock;
            Library = library;
            Channels = channels;
            Status = status;
            Disk = disk;
            Storage = storage;
            View = view;
        }

        public FakeTimeProvider Clock { get; }

        public RecordingLibrary Library { get; }

        public ChannelStore Channels { get; }

        public DvrStatus Status { get; }

        public MutableDiskSpace Disk { get; }

        public StorageMonitor Storage { get; }

        public LibraryView View { get; }

        public static async Task<Harness> CreateAsync(params string[] logins)
        {
            string root = Path.Combine(
                Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

            var storageOptions = Options.Create(new StorageOptions
            {
                RecordingsRoot = root,
                MinFreeGb = 1,
            });

            var channels = new ChannelStore(
                Options.Create(new TwitchOptions()),
                storageOptions,
                NullLogger<ChannelStore>.Instance);

            await channels.LoadAsync(CancellationToken.None);

            foreach (string login in logins)
                await channels.AddAsync(login, CancellationToken.None);

            var clock = new FakeTimeProvider(Now);
            var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
            var status = new DvrStatus();
            var disk = new MutableDiskSpace(
                freeBytes: 50L * 1024 * 1024 * 1024,
                totalBytes: 100L * 1024 * 1024 * 1024);
            var storage = new StorageMonitor(
                new DiskGuard(storageOptions, disk),
                library,
                clock,
                NullLogger<StorageMonitor>.Instance);
            var view = new LibraryView(library, channels, status, storage, clock);

            return new Harness(root, clock, library, channels, status, disk, storage, view);
        }

        public void Add(RecordingSummary summary)
        {
            Library.Upsert(summary);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        public async Task SetProfileAsync(
            string login, string displayName, string? avatarUrl = null)
        {
            await Channels.SetProfileAsync(
                login, userId: "1001", displayName, avatarUrl, CancellationToken.None);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        public async Task SetPausedAsync(string login, bool paused)
        {
            await Channels.SetPausedAsync(login, paused, CancellationToken.None);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        public void MarkActive(RecordingSummary summary)
        {
            var live = new LiveStream(
                "12345", summary.Title, summary.Game, summary.StartedAt, ThumbnailUrl: null);
            var session = new Session(
                summary.Id,
                summary.Channel,
                live.StreamId,
                summary.StartedAt,
                Path.Combine(_root, summary.Channel, summary.Id));

            Status.Applied(
                summary.Channel,
                null,
                new DvrState.Recording(
                    session,
                    new JobId(summary.Id, 1),
                    live,
                    Attempt: 0,
                    HasMedia: true,
                    Streak: 0),
                Clock.GetUtcNow());
        }

        public void Dispose()
        {
            View.Dispose();
            Storage.Dispose();

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}

internal sealed class MutableDiskSpace(long freeBytes, long totalBytes) : IDiskSpace
{
    public long FreeBytes { get; set; } = freeBytes;

    public long TotalBytes { get; set; } = totalBytes;

    public long AvailableFreeSpace(string path) => FreeBytes;

    public long TotalSize(string path) => TotalBytes;
}
