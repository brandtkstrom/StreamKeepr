using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Mock;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Mock;

public sealed class MockEnvironmentTests
{
    [Fact]
    public void Accepts_a_root_under_the_system_temp_directory()
    {
        string temp = Path.GetTempPath();
        string root = Path.Combine(temp, "streamkeepr-tests", Guid.NewGuid().ToString("N"));

        string resolved = MockEnvironment.ResolveRecordingsRoot(root, temp);

        Assert.Equal(Path.GetFullPath(root), resolved);
    }

    [Fact]
    public void Defaults_to_a_streamkeepr_directory_under_temp()
    {
        string temp = Path.GetTempPath();

        string resolved = MockEnvironment.ResolveRecordingsRoot(null, temp);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp, MockEnvironment.DataDirectoryName,
                MockEnvironment.RecordingsDirectoryName)),
            resolved);
    }

    [Fact]
    public void Refuses_a_repository_path_so_mock_never_touches_production_data()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MockEnvironment.ResolveRecordingsRoot(TestPaths.RepositoryRoot(), Path.GetTempPath()));
    }

    [Fact]
    public void Refuses_the_temp_root_itself_so_reset_cannot_delete_it()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MockEnvironment.ResolveRecordingsRoot(Path.GetTempPath(), Path.GetTempPath()));
    }

    [Fact]
    public void Each_process_start_gets_its_own_record_store_beside_the_recordings_root()
    {
        string temp = Path.GetTempPath();

        string first = MockEnvironment.NewRecordStorePath(temp);
        string second = MockEnvironment.NewRecordStorePath(temp);

        Assert.NotEqual(first, second);
        string parent = Path.GetFullPath(Path.Combine(temp, MockEnvironment.DataDirectoryName));
        Assert.Equal(parent, Path.GetDirectoryName(Path.GetDirectoryName(first)));
        Assert.False(
            first.StartsWith(MockEnvironment.ResolveRecordingsRoot(null, temp), StringComparison.Ordinal),
            "the seeder may reset the recordings root, so the database must live outside it");
    }

    [Fact]
    public async Task The_record_store_is_gone_after_a_clean_shutdown()
    {
        string database = MockEnvironment.NewRecordStorePath(Path.GetTempPath());
        string directory = Path.GetDirectoryName(database)!;
        Directory.CreateDirectory(Path.Combine(directory, "backups"));
        await File.WriteAllTextAsync(database, "db");
        await File.WriteAllTextAsync(database + "-wal", "wal");

        await using WebApplication app = BuildApp(database);
        MockEnvironment.DeleteRecordStoreOnCleanShutdown(app);
        await app.StartAsync();
        Assert.True(File.Exists(database));

        await app.StopAsync();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Refuses_to_delete_a_database_it_did_not_create()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "streamkeepr.db");
        await File.WriteAllTextAsync(database, "db");
        await using WebApplication app = BuildApp(database);

        Assert.Throws<InvalidOperationException>(() => MockEnvironment.DeleteRecordStoreOnCleanShutdown(app));
        Assert.True(File.Exists(database));
    }

    private static WebApplication BuildApp(string databasePath)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<StorageOptions>(options => options.DatabasePath = databasePath);
        return builder.Build();
    }

    [Fact]
    public void Catalog_has_four_streamers_and_eight_deterministic_recordings()
    {
        Assert.Equal(4, MockCatalog.Streamers.Count);
        Assert.Equal(4, MockCatalog.Streamers.Select(streamer => streamer.Login).Distinct().Count());
        Assert.Equal(8, MockCatalog.Recordings.Count);

        Assert.All(MockCatalog.Streamers, streamer =>
            Assert.Contains(MockCatalog.Recordings, recording => recording.Channel == streamer.Login));

        var ids = MockCatalog.Recordings
            .Select(recording => MockCatalog.SessionFor(
                recording, Path.Combine(Path.GetTempPath(), "streamkeepr-tests", "catalog")).Id)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(MockCatalog.T0, MockCatalog.Recordings.Min(recording => recording.StartedAt));

        Assert.Equal(2, MockCatalog.Recordings.Count(recording => !recording.Complete));
        Assert.Contains(
            MockCatalog.Recordings,
            recording => !recording.Complete && recording.Parts == 2);
        Assert.Equal("mock_rain", MockCatalog.Growing.Channel);
        Assert.Contains(MockCatalog.Recordings, recording => recording.Complete && recording.Parts == 2);
        Assert.Contains(MockCatalog.Recordings, recording => recording.Complete && recording.Parts == 1);
        Assert.Contains(MockCatalog.Recordings, recording => recording.Chapters.Count > 0);
        Assert.NotNull(MockCatalog.Selected);
    }
}

public sealed class MockTwitchTests
{
    [Fact]
    public void Defaults_to_the_growing_capture_live_and_other_channels_offline()
    {
        var twitch = new MockTwitch();
        MockRecording growing = MockCatalog.Growing;

        var observation = Assert.IsType<StreamObservation.Live>(
            twitch.Observe(growing.Channel));
        Assert.Equal(growing.StreamId, observation.Stream.StreamId);

        foreach (MockRecording recording in MockCatalog.Recordings.Where(
                     recording => recording.Channel != growing.Channel))
        {
            Assert.IsType<StreamObservation.Offline>(twitch.Observe(recording.Channel));
        }
    }

    [Fact]
    public void Test_controls_replace_observations_deterministically()
    {
        var twitch = new MockTwitch();

        twitch.SetLive("mock_nova");
        Assert.IsType<StreamObservation.Live>(twitch.Observe("mock_nova"));

        twitch.SetUnknown("mock_nova", "simulated outage");
        var unknown = Assert.IsType<StreamObservation.Unknown>(twitch.Observe("mock_nova"));
        Assert.Equal("simulated outage", unknown.Error);

        twitch.SetOffline("mock_nova");
        Assert.IsType<StreamObservation.Offline>(twitch.Observe("mock_nova"));
    }

    [Fact]
    public void Resolves_catalog_profiles_and_rejects_unknown_logins()
    {
        var twitch = new MockTwitch();
        MockStreamer streamer = MockCatalog.Streamers[0];

        ChannelProfile? profile = twitch.FindProfile(streamer.Login);

        Assert.NotNull(profile);
        Assert.Equal(streamer.UserId, profile.UserId);
        Assert.Equal(streamer.DisplayName, profile.DisplayName);
        Assert.Null(twitch.FindProfile("not_a_mock_channel"));

        MockStreamer growingStreamer = MockCatalog.Streamers.Single(
            streamer => streamer.Login == MockCatalog.Growing.Channel);
        Assert.Equal(
            MockCatalog.Growing.Title,
            twitch.FindChannelInfo(growingStreamer.UserId)?.Title);
    }
}

public sealed class MockDiskSpaceTests
{
    [Fact]
    public void Advances_by_two_percent_and_stops_at_five_percent_free()
    {
        var disk = new MockDiskSpace();

        Assert.Equal(MockDiskSpace.TotalBytes, disk.AvailableFreeSpace(Path.GetTempPath()));

        disk.Advance();
        Assert.Equal(
            MockDiskSpace.TotalBytes - MockDiskSpace.TotalBytes / 50,
            disk.AvailableFreeSpace(Path.GetTempPath()));

        for (int index = 0; index < 100; index++)
            disk.Advance();

        Assert.Equal(MockDiskSpace.TotalBytes / 20, disk.AvailableFreeSpace(Path.GetTempPath()));
        Assert.Equal(MockDiskSpace.TotalBytes, disk.TotalSize(Path.GetTempPath()));
    }
}

public sealed class MockEventSubClientTests
{
    [Fact]
    public async Task Routes_a_test_controlled_push_once_and_stops_on_shutdown()
    {
        var twitch = new MockTwitch();
        var clock = new FakeTimeProvider(MockCatalog.T0);
        var schedule = new PollSchedule(clock);
        using var records = new TestRecordStore();
        ChannelStore channels = await records.LoadChannelStoreAsync();
        var client = new MockEventSubClient(
            twitch,
            schedule,
            channels,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MockEventSubClient>.Instance);

        var routed = new TaskCompletionSource<(string Login, StreamObservation Observation)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(
            (login, observation, _) =>
            {
                routed.TrySetResult((login, observation));
                return ValueTask.CompletedTask;
            },
            cts.Token);

        await twitch.PushAsync(
            "mock_echo", new StreamObservation.Unknown("push outage"), cts.Token);

        (string login, StreamObservation observation) =
            await routed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("mock_echo", login);
        Assert.IsType<StreamObservation.Unknown>(observation);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public sealed class MockStreamPollerTests
{
    [Fact]
    public async Task Live_polls_carry_a_deterministic_viewer_count_and_offline_polls_none()
    {
        MockRecording growing = MockCatalog.Growing;
        var clock = new FakeTimeProvider(growing.StartedAt.AddMinutes(5));

        IReadOnlyDictionary<string, StreamObservation> first = await PollOnceAsync(clock);
        IReadOnlyDictionary<string, StreamObservation> second = await PollOnceAsync(clock);

        int? count = Assert.IsType<StreamObservation.Live>(first[growing.Channel]).Stream.ViewerCount;
        Assert.NotNull(count);
        Assert.InRange(count.Value, 1, 100_000);
        Assert.Equal(count, Assert.IsType<StreamObservation.Live>(second[growing.Channel]).Stream.ViewerCount);
        Assert.IsType<StreamObservation.Offline>(first["mock_nova"]);
    }

    private static async Task<IReadOnlyDictionary<string, StreamObservation>> PollOnceAsync(TimeProvider clock)
    {
        var poller = new MockStreamPoller(
            new MockTwitch(),
            new PollSchedule(clock),
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MockStreamPoller>.Instance);
        var observed = new Dictionary<string, StreamObservation>(StringComparer.Ordinal);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        string[] logins = [MockCatalog.Growing.Channel, "mock_nova"];

        Task run = poller.RunAsync(
            () => logins,
            (login, observation, _) =>
            {
                observed[login] = observation;
                if (observed.Count == logins.Length)
                    polled.TrySetResult();
                return ValueTask.CompletedTask;
            },
            cts.Token);

        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        return observed;
    }
}

public sealed class MockLiveFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Produces_start_end_delete_and_storage_changes()
    {
        using var harness = new FeedHarness(_root);
        harness.CreateMarker();
        RecordingSummary target = harness.CreateDeleteTarget();

        await harness.StartAsync();
        await AdvanceToStartAsync(harness);
        Assert.True(harness.Disk.AvailableFreeSpace(_root) < MockDiskSpace.TotalBytes);

        await AdvanceToEndAsync(harness);
        await AdvanceToDeleteAsync(harness);
        await WaitUntilAsync(() => !Directory.Exists(harness.DeletedDirectory));

        Assert.True(harness.StorageChanges > 0);
        Assert.Null(harness.Library.Find(target.Id));

        await harness.StopAsync();
    }

    [Fact]
    public async Task Refuses_to_delete_without_the_mock_seeded_marker()
    {
        using var harness = new FeedHarness(_root);
        RecordingSummary target = harness.CreateDeleteTarget();

        await harness.StartAsync();
        await AdvanceToStartAsync(harness);
        await AdvanceToEndAsync(harness);
        await AdvanceToDeleteAsync(harness);

        Assert.True(Directory.Exists(harness.DeletedDirectory));
        Assert.NotNull(harness.Library.Find(target.Id));

        await harness.StopAsync();
    }

    private static async Task AdvanceToStartAsync(FeedHarness harness)
    {
        await AdvanceOneTickAsync(harness, tick: 1);
        await WaitUntilAsync(
            () => harness.Twitch.Observe("mock_echo") is StreamObservation.Live);
    }

    private static async Task AdvanceToEndAsync(FeedHarness harness)
    {
        await AdvanceOneTickAsync(harness, tick: 2);
        await WaitUntilAsync(
            () => harness.Twitch.Observe("mock_echo") is StreamObservation.Offline);
    }

    private async Task AdvanceToDeleteAsync(FeedHarness harness)
    {
        long freeBefore = harness.Disk.AvailableFreeSpace(_root);
        await AdvanceOneTickAsync(harness, tick: 3);
        await WaitUntilAsync(
            () => harness.Disk.AvailableFreeSpace(_root) < freeBefore);
    }

    /// <summary>
    /// Advances the fake clock by one feed tick, but only once the feed has armed the delay for
    /// that tick. ExecuteAsync runs on the thread pool, so StartAsync (and each tick's work) can
    /// return before the feed reaches Task.Delay; advancing earlier would be lost, because the
    /// late timer is then due one tick after the already-advanced time.
    /// </summary>
    private static async Task AdvanceOneTickAsync(FeedHarness harness, int tick)
    {
        await WaitUntilAsync(() => harness.FeedTimersCreated >= tick);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 20_000; attempt++)
        {
            if (condition())
                return;

            // Yield normally, but periodically release the thread so the feed's fake-timer
            // continuation can be scheduled while the rest of the suite runs in parallel.
            if ((attempt & 0xF) == 0)
                await Task.Delay(1);
            else
                await Task.Yield();
        }

        throw new TimeoutException("The Mock live feed did not reach the expected state.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FeedHarness : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _storageChanges;
        private int _feedTimersCreated;

        public FeedHarness(string root)
        {
            var storageOptions = Options.Create(new StorageOptions
            {
                RecordingsRoot = root,
                MinFreeGb = 1,
            });
            var recorder = Options.Create(new RecorderOptions { SegmentSeconds = 2 });
            var sessions = new SessionStore(
                recorder,
                Library,
                Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionStore>.Instance, TestRetry.Immediate);

            Storage = new StorageMonitor(
                new DiskGuard(storageOptions, Disk),
                Library,
                Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageMonitor>.Instance);
            Storage.Changed += () => Interlocked.Increment(ref _storageChanges);

            var options = Options.Create(new MockLiveFeedOptions
            {
                TickSeconds = 1,
                StartSeconds = 1,
                EndSeconds = 2,
                DeleteSeconds = 3,
            });

            Feed = new MockLiveFeed(
                Twitch,
                Disk,
                Library,
                sessions,
                Storage,
                storageOptions,
                options,
                new TimerCountingClock(Clock, () => Interlocked.Increment(ref _feedTimersCreated)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MockLiveFeed>.Instance);

            Deleted = MockCatalog.Recordings
                .Where(recording => recording.Complete && recording.Channel == "mock_echo")
                .OrderBy(recording => recording.StartedAt)
                .First();
            DeletedSession = MockCatalog.SessionFor(Deleted, root);
            MarkerPath = Path.Combine(
                AppDataPaths.AppDataDirectory(root), "mock-seeded");
        }

        public FakeTimeProvider Clock { get; } = new(MockCatalog.T0);

        public MockTwitch Twitch { get; } = new();

        public MockDiskSpace Disk { get; } = new();

        public RecordingLibrary Library { get; } = new(
            new FakeTimeProvider(MockCatalog.T0),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordingLibrary>.Instance);

        public StorageMonitor Storage { get; }

        public MockLiveFeed Feed { get; }

        public MockRecording Deleted { get; }

        public Session DeletedSession { get; }

        public string DeletedDirectory => DeletedSession.Directory;

        public string MarkerPath { get; }

        public int StorageChanges => Volatile.Read(ref _storageChanges);

        /// <summary>
        /// Timers the feed has created on the fake clock; one per tick's Task.Delay. Counted on a
        /// feed-only view of the clock because StorageMonitor arms its own timer on the same clock.
        /// </summary>
        public int FeedTimersCreated => Volatile.Read(ref _feedTimersCreated);

        public void CreateMarker()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, "1");
        }

        public RecordingSummary CreateDeleteTarget()
        {
            Directory.CreateDirectory(DeletedDirectory);
            File.WriteAllText(Path.Combine(DeletedDirectory, "keep.txt"), "x");

            var summary = new RecordingSummary(
                DeletedSession.Id,
                DeletedSession.Channel,
                Deleted.Title,
                Deleted.Game,
                DeletedSession.StartedAt,
                DeletedSession.StartedAt.AddHours(1),
                IsRecording: false,
                DurationSeconds: 3600,
                SizeBytes: 1024,
                PartCount: 1,
                SegmentCount: 1,
                HasThumbnail: false,
                Chapters: []);

            Library.Upsert(summary);
            return summary;
        }

        public async Task StartAsync()
        {
            await Feed.StartAsync(_cts.Token);
            await Task.Yield();
        }

        public async Task StopAsync()
        {
            await _cts.CancelAsync();
            await Feed.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            _cts.Cancel();
            Feed.Dispose();
            Storage.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>Delegates to the shared fake clock and reports each timer it creates.</summary>
    private sealed class TimerCountingClock(TimeProvider inner, Action onTimerCreated) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            onTimerCreated();
            return timer;
        }
    }
}

public sealed class MockSeederTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

    // Each seeder gets its own record store outside the root, like the per-process Mock database.
    private readonly List<TestRecordStore> _records = [];

    [Fact]
    public async Task Seeds_four_channels_and_eight_recordings_including_a_growing_capture()
    {
        (MockSeeder seeder, ChannelStore channels, RecordingLibrary library) = CreateSeeder();

        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(4, channels.Snapshot.Count);
        Assert.Equal(8, library.All.Count);
        Assert.All(
            MockCatalog.Streamers,
            streamer => Assert.Contains(library.All, summary => summary.Channel == streamer.Login));

        RecordingSummary growing = Assert.Single(
            library.All,
            summary => summary.IsRecording && summary.Id ==
                MockCatalog.SessionFor(MockCatalog.Growing, _root).Id);
        Assert.Equal(MockCatalog.Growing.Channel, growing.Channel);
        Assert.Equal(1, growing.PartCount);
        Assert.True(growing.HasThumbnail);

        Assert.Equal(2, library.All.Count(summary => summary.IsRecording));
        Assert.Contains(
            library.All,
            summary => summary.IsRecording && summary.PartCount == 2);
        Assert.Contains(
            library.All,
            summary => !summary.IsRecording && summary.PartCount == 2);
        Assert.Contains(
            library.All,
            summary => !summary.IsRecording && summary.Chapters.Count > 0);

        string growingManifest = Path.Combine(
            MockCatalog.SessionFor(MockCatalog.Growing, _root).Directory,
            SessionStore.ManifestFileName);
        Assert.True(File.Exists(growingManifest));
    }

    [Fact]
    public async Task Seeds_viewer_samples_for_finished_recordings_only()
    {
        (MockSeeder seeder, _, RecordingLibrary library) = CreateSeeder(
            new MockSeedOptions { ScaleRecordings = 2 });

        await seeder.StartAsync(CancellationToken.None);

        foreach (RecordingSummary summary in library.All)
        {
            IReadOnlyList<ViewerSample> samples = await ViewerSamples.ReadAsync(
                Path.Combine(_root, summary.Channel, summary.Id),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

            if (summary.IsRecording)
            {
                Assert.Empty(samples);
                continue;
            }

            // A 60 s part every 10 s: 0, 10, …, 60 → seven samples per part.
            double duration = summary.DurationSeconds!.Value;
            Assert.Equal((int)(duration / 10) + 1, samples.Count);
            Assert.Equal(0, samples[0].At);
            Assert.InRange(samples[^1].At, duration - 10, duration);
            Assert.All(samples, sample => Assert.InRange(sample.Count, 1, 100_000));
            Assert.True(samples.Select(sample => sample.Count).Distinct().Count() > 1, "the chart has a shape");
        }
    }

    [Fact]
    public async Task Scale_seeding_adds_finished_archive_recordings_across_every_streamer()
    {
        (MockSeeder seeder, _, RecordingLibrary library) = CreateSeeder(
            new MockSeedOptions { ScaleRecordings = 40 });

        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(48, library.All.Count);
        Assert.Equal(2, library.All.Count(summary => summary.IsRecording));
        RecordingSummary[] archive = library.All
            .Where(summary => summary.Title.StartsWith("Archive stream", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(40, archive.Length);
        Assert.All(archive, summary =>
        {
            Assert.False(summary.IsRecording);
            Assert.True(summary.HasThumbnail);
            Assert.True(summary.StartedAt < MockCatalog.T0);
        });
        Assert.All(
            MockCatalog.Streamers,
            streamer => Assert.Equal(10, archive.Count(summary => summary.Channel == streamer.Login)));
        Assert.True(
            archive.Select(summary => summary.StartedAt.UtcDateTime.Date).Distinct().Count() > 10,
            "the archive spreads over many days so the drawer gets many day headers");
    }

    [Fact]
    public async Task Keeping_existing_recordings_still_seeds_the_fresh_channel_list()
    {
        (MockSeeder first, _, _) = CreateSeeder();
        await first.StartAsync(CancellationToken.None);

        (MockSeeder restarted, ChannelStore channels, _) =
            CreateSeeder(new MockSeedOptions { ResetOnStart = false });
        await restarted.StartAsync(CancellationToken.None);

        Assert.Equal(
            MockCatalog.Streamers.Select(streamer => streamer.Login),
            channels.Snapshot.Select(entry => entry.Login));
    }

    [Fact]
    public async Task Refuses_to_reset_a_root_with_unrelated_data()
    {
        Directory.CreateDirectory(_root);
        string unrelated = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        (MockSeeder seeder, _, _) = CreateSeeder();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.StartAsync(CancellationToken.None));

        Assert.Contains("Refusing to reset", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Resets_a_root_that_contains_only_the_app_data_directory()
    {
        string appData = Path.Combine(_root, AppDataPaths.AppDataDirectoryName);
        Directory.CreateDirectory(appData);
        string leftover = Path.Combine(appData, "leftover");
        await File.WriteAllTextAsync(leftover, "x");

        (MockSeeder seeder, ChannelStore channels, _) = CreateSeeder();

        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(4, channels.Snapshot.Count);
        Assert.False(File.Exists(leftover));
    }

    private (MockSeeder Seeder, ChannelStore Channels, RecordingLibrary Library) CreateSeeder(
        MockSeedOptions? seedOptions = null)
    {
        string ffmpeg = TestPaths.RequireExecutable(
            "ffmpeg", "ffmpeg is required for Mock seeding tests. Install it and put it on PATH.");

        var storage = Options.Create(new StorageOptions { RecordingsRoot = _root, MinFreeGb = 1 });
        var recorder = Options.Create(new RecorderOptions
        {
            FfmpegPath = ffmpeg,
            StreamlinkPath = TestPaths.FixturePath("fake-live-streamlink"),
            SegmentSeconds = 2,
            HealthyPartSeconds = 5,
        });
        var twitch = Options.Create(new TwitchOptions());
        var records = new TestRecordStore();
        _records.Add(records);
        ChannelStore channels = records.CreateChannelStore(
            records.StartAsync().GetAwaiter().GetResult());
        var library = new RecordingLibrary(
            new FakeTimeProvider(MockCatalog.T0),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordingLibrary>.Instance);
        var sessions = new SessionStore(
            recorder, library, new FakeTimeProvider(MockCatalog.T0),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionStore>.Instance, TestRetry.Immediate);

        var seeder = new MockSeeder(
            storage,
            recorder,
            Options.Create(seedOptions ?? new MockSeedOptions()),
            channels,
            sessions,
            library,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MockSeeder>.Instance);

        return (seeder, channels, library);
    }

    public void Dispose()
    {
        foreach (TestRecordStore records in _records)
            records.Dispose();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
