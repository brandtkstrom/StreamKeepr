using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Storage;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Runtime;

public sealed class DvrSupervisorTests
{
    private const string Channel = "streamer";

    [Fact]
    public async Task A_faulted_channel_loop_stops_the_host()
    {
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            var lifetime = new FakeHostApplicationLifetime();
            var logs = new ListLoggerProvider();
            using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
            using DvrSupervisor supervisor = CreateSupervisor(
                root, poller, lifetime, loggerFactory, [Channel]);

            await supervisor.StartAsync(CancellationToken.None);
            await poller.Started.WaitAsync(TimeSpan.FromSeconds(5));

            poller.Send(Channel, new StreamObservation.Live(
                new LiveStream(
                    "123456789",
                    "A stream",
                    "Just Chatting",
                    new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero),
                    ThumbnailUrl: null)));

            await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(logs.Messages,
                message => message.Contains("crashed", StringComparison.Ordinal));

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Channel_control_actions_log_the_channel_and_action()
    {
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            poller.Profiles["new_channel"] = new ChannelProfile(
                "123456", "New Channel", "https://example/avatar.png");
            var logs = new ListLoggerProvider();
            using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
            using DvrSupervisor supervisor = CreateSupervisor(
                root, poller, new FakeHostApplicationLifetime(), loggerFactory, []);
            await supervisor.StartAsync(CancellationToken.None);
            await poller.Started.WaitAsync(TimeSpan.FromSeconds(5));

            await supervisor.AddChannelAsync(" New_Channel ", CancellationToken.None);
            await supervisor.StopRecordingAsync("new_channel", CancellationToken.None);
            await supervisor.ResumeRecordingAsync("new_channel", CancellationToken.None);
            await supervisor.RemoveChannelAsync("new_channel", CancellationToken.None);

            CapturedLogEntry[] actions = logs.Entries
                .Where(entry => entry.Properties.ContainsKey("Action"))
                .ToArray();
            Assert.Equal(
                ["Add", "Stop", "Resume", "Remove"],
                actions.Select(entry => entry.Properties["Action"]?.ToString()));
            Assert.All(actions, entry =>
            {
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Equal("new_channel", entry.Properties["Channel"]);
            });

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task A_channel_added_inside_a_request_does_not_carry_its_log_scope_or_activity()
    {
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            poller.Profiles["new_channel"] = new ChannelProfile(
                "123456", "New Channel", "https://example/avatar.png");
            var logs = new ListLoggerProvider();
            using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
            ILogger probe = loggerFactory.CreateLogger("Probe");
            Activity? recorderActivity = null;
            var recorderRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recorder = new FakeRecorder
            {
                OnRun = async (_, stopRequested) =>
                {
                    recorderActivity = Activity.Current;
                    probe.LogInformation("Recorder ran.");
                    recorderRan.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, stopRequested);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return new PartOutcome(RecorderExitKind.Stopped, "stopped");
                },
            };
            using DvrSupervisor supervisor = CreateSupervisor(
                root, poller, new FakeHostApplicationLifetime(), loggerFactory, [], recorder: recorder);
            await supervisor.StartAsync(CancellationToken.None);
            await poller.Started.WaitAsync(TimeSpan.FromSeconds(5));

            // Stands in for the Blazor circuit's request: its logging scope and activity are
            // ambient while the channel is added, and must stay with the request.
            using (var request = new Activity("request").Start())
            using (probe.BeginScope(new Dictionary<string, object> { ["RequestPath"] = "/_blazor" }))
            {
                await supervisor.AddChannelAsync("new_channel", CancellationToken.None);
            }

            poller.Send("new_channel", new StreamObservation.Live(
                new LiveStream(
                    "123456789",
                    "A stream",
                    "Just Chatting",
                    new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero),
                    ThumbnailUrl: null)));
            await recorderRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

            CapturedLogEntry ran = Assert.Single(
                logs.Entries, entry => entry.Message == "Recorder ran.");
            Assert.DoesNotContain("RequestPath", ran.Scopes.Keys);
            Assert.Null(recorderActivity);

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Failed_channel_control_actions_log_no_action()
    {
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            var logs = new ListLoggerProvider();
            using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
            using DvrSupervisor supervisor = CreateSupervisor(
                root, poller, new FakeHostApplicationLifetime(), loggerFactory, [Channel]);
            await supervisor.StartAsync(CancellationToken.None);
            await poller.Started.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<UnknownChannelException>(
                () => supervisor.AddChannelAsync("missing", CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => supervisor.AddChannelAsync(Channel, CancellationToken.None));
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => supervisor.StopRecordingAsync("missing", CancellationToken.None));

            Assert.DoesNotContain(logs.Entries, entry => entry.Properties.ContainsKey("Action"));

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Startup_returns_before_profile_lookup_and_recovery_finish()
    {
        string root = NewRoot();

        try
        {
            var lookupGate = new TaskCompletionSource<ChannelProfile?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var poller = new ScriptedStreamPoller { Lookup = (_, _) => lookupGate.Task };
            var library = new RecordingLibrary(TimeProvider.System, NullLogger<RecordingLibrary>.Instance);
            var status = new DvrStatus();
            using DvrSupervisor supervisor = CreateSupervisor(
                root,
                poller,
                new FakeHostApplicationLifetime(),
                NullLoggerFactory.Instance,
                [Channel],
                library,
                status);

            // The web server starts only after every hosted service's StartAsync returns, so it
            // must not wait on Twitch or on the recordings scan.
            await supervisor.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(library.IsLoaded);
            Assert.Empty(status.Current);
            Assert.False(poller.Started.IsCompleted);

            lookupGate.SetResult(null);
            await poller.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(library.IsLoaded);
            await RuntimeTestWait.ForStatusAsync(status, Channel, _ => true);

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Channel_control_waits_until_startup_recovery_has_started_the_channels()
    {
        string root = NewRoot();

        try
        {
            var lookupGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var poller = new ScriptedStreamPoller();
            poller.Lookup = async (login, ct) =>
            {
                if (login == Channel)
                    await lookupGate.Task.WaitAsync(ct);
                return new ChannelProfile("123456", login, "https://example/avatar.png");
            };
            var library = new RecordingLibrary(TimeProvider.System, NullLogger<RecordingLibrary>.Instance);
            using DvrSupervisor supervisor = CreateSupervisor(
                root,
                poller,
                new FakeHostApplicationLifetime(),
                NullLoggerFactory.Instance,
                [Channel],
                library);
            await supervisor.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Task add = supervisor.AddChannelAsync("new_channel", CancellationToken.None);
            Task stop = supervisor.StopRecordingAsync(Channel, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(add.IsCompleted);
            Assert.False(stop.IsCompleted);

            lookupGate.SetResult();
            await Task.WhenAll(add, stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(library.IsLoaded);

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Startup_recovery_failure_stops_the_host_without_starting_channels()
    {
        string root = NewRoot();

        try
        {
            // Two unfinished sessions: the older one must be finalized, and its finalize fails
            // because a directory sits where the index temp file goes.
            await SeedUnfinishedAsync(root, "987654321", hoursAfterStart: 0);
            string older = Path.Combine(root, Channel, OnlySessionFolder(root));
            await SeedUnfinishedAsync(root, "987654322", hoursAfterStart: 1);
            Directory.CreateDirectory(Path.Combine(older, SessionStore.IndexFileName + ".tmp"));

            var poller = new ScriptedStreamPoller();
            var lifetime = new FakeHostApplicationLifetime();
            var logs = new ListLoggerProvider();
            using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
            var library = new RecordingLibrary(TimeProvider.System, NullLogger<RecordingLibrary>.Instance);
            var status = new DvrStatus();
            using DvrSupervisor supervisor = CreateSupervisor(
                root, poller, lifetime, loggerFactory, [Channel], library, status);

            await supervisor.StartAsync(CancellationToken.None);
            await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(logs.Entries, entry =>
                entry.Level == LogLevel.Critical
                && entry.Message.Contains("recovering sessions", StringComparison.Ordinal));
            Assert.False(library.IsLoaded);
            Assert.Empty(status.Current);
            Assert.False(poller.Started.IsCompleted);

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task SeedUnfinishedAsync(string root, string streamId, int hoursAfterStart)
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var store = new SessionStore(
            Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
            new RecordingLibrary(clock, NullLogger<RecordingLibrary>.Instance),
            clock,
            NullLogger<SessionStore>.Instance,
            TestRetry.Immediate);
        var live = new LiveStream(
            streamId,
            "A stream",
            "Just Chatting",
            clock.GetUtcNow().AddHours(hoursAfterStart),
            ThumbnailUrl: null);
        Session session = Session.For(live, Channel, root);
        await store.OpenAsync(session, live, CancellationToken.None);
        File.WriteAllText(
            Path.Combine(session.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                2,
                "init_001.mp4",
                PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 2)));
        File.WriteAllBytes(Path.Combine(session.Directory, "init_001.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(session.Directory, "p001_00001.m4s"), [4, 5, 6]);
    }

    private static string OnlySessionFolder(string root) =>
        Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(root, Channel))));

    /// <summary>
    /// A supervisor over real stores in <paramref name="root"/>, watching
    /// <paramref name="channels"/>. Its fake recorder publishes an exit the reducer does not
    /// expect, so a live observation crashes the channel loop; tests that never send one are
    /// unaffected.
    /// </summary>
    private static DvrSupervisor CreateSupervisor(
        string root,
        ScriptedStreamPoller poller,
        FakeHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        string[] channels,
        RecordingLibrary? library = null,
        DvrStatus? status = null,
        FakeRecorder? recorder = null)
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        IOptions<StorageOptions> storageOptions = Options.Create(
            new StorageOptions { RecordingsRoot = root, MinFreeGb = 1 });
        library ??= new RecordingLibrary(clock, NullLogger<RecordingLibrary>.Instance);
        var store = new SessionStore(
            Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
            library,
            clock,
            NullLogger<SessionStore>.Instance, TestRetry.Immediate);
        var disk = new DiskGuard(
            storageOptions,
            new FakeDiskSpace(DiskGuard.BytesPerGib * 2, DiskGuard.BytesPerGib * 4));
        status ??= new DvrStatus();
        recorder ??= new FakeRecorder
        {
            OnRun = (_, _) => Task.FromResult(new PartOutcome(RecorderExitKind.Exited, "no media")),
        };
        var factory = new ChannelCoordinatorFactory(
            Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
            storageOptions,
            recorder,
            store,
            disk,
            status,
            new ThrowingRetryJitter(),
            clock,
            NullLoggerFactory.Instance);
        var records = new TestRecordStore(storageOptions.Value.RecordingsRoot);
        ChannelStore channelStore = records.CreateChannelStore(
            records.StartAsync().GetAwaiter().GetResult(), [.. channels]);
        var recovery = new RecoveryService(
            store, library, storageOptions, clock, NullLogger<RecoveryService>.Instance);

        return new DvrSupervisor(
            poller,
            new NoopEventSubClient(),
            recovery,
            channelStore,
            status,
            factory,
            lifetime,
            clock,
            loggerFactory.CreateLogger<DvrSupervisor>());
    }

    private static string NewRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class NoopEventSubClient : IEventSubClient
    {
        public string Transport => "Test";

        public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }
}
