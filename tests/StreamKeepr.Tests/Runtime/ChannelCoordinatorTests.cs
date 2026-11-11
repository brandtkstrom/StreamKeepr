using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Runtime;

public sealed class ChannelCoordinatorTests
{
    private const string Channel = "streamer";

    [Fact]
    public async Task Live_opens_the_session_and_starts_the_recorder()
    {
        using var context = new Context();
        var started = new TaskCompletionSource<RecorderCall>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        context.Recorder.OnRun = async (call, stopRequested) =>
        {
            WritePart(call.Session, 1);
            await call.PublishAsync(new DvrEvent.PartProducingMedia(
                call.Job, context.Clock.GetUtcNow()));
            started.TrySetResult(call);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopRequested);
            }
            catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
            {
            }

            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Stopped, "stopped"));
        };

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        RecorderCall call = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.Recording);

        // StartRecorder is only reachable after OpenSession writes the manifest.
        Assert.True(File.Exists(Path.Combine(context.SessionDirectory, SessionStore.ManifestFileName)));
        Assert.Equal(call.Job, snapshot.ActiveJob);

        call.ReportProgress?.Invoke(call.Job, 4096, TimeSpan.FromSeconds(2));
        snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.PartBytes == 4096);
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.PartMediaTime);

        await context.Coordinator.PublishAsync(new DvrEvent.StopRequested());
        await context.StopAsync();

        string index = await File.ReadAllTextAsync(
            Path.Combine(context.SessionDirectory, SessionStore.IndexFileName));
        Assert.Contains("#EXT-X-ENDLIST", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_after_a_failed_part_finalizes_the_session()
    {
        using var context = new Context();
        context.Recorder.OnRun = async (call, _) =>
        {
            WritePart(call.Session, 1);
            await call.PublishAsync(new DvrEvent.PartProducingMedia(
                call.Job, context.Clock.GetUtcNow()));
            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Exited, "stream ended"));
        };

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        _ = await context.Recorder.Started.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Offline(), context.Clock.GetUtcNow()));
        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Offline(), context.Clock.GetUtcNow()));

        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status,
            Channel,
            status => status.State is DvrState.Idle { Finished: not null });
        await context.StopAsync();

        Assert.Equal("stream ended", snapshot.LastDetail);
        string index = await File.ReadAllTextAsync(
            Path.Combine(context.SessionDirectory, SessionStore.IndexFileName));
        Assert.Contains("#EXT-X-ENDLIST", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_failure_skips_the_recorder_and_surfaces_the_failure()
    {
        using var context = new Context();
        await File.WriteAllTextAsync(
            Path.Combine(context.Root, Channel), "not a directory");

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);

        Assert.Equal("open failed", snapshot.LastDetail);
        Assert.False(string.IsNullOrEmpty(snapshot.LastError));
        Assert.False(context.Recorder.Started.TryRead(out _));

        await context.StopAsync();
    }

    [Fact]
    public async Task Reopen_failure_skips_the_recorder_and_surfaces_the_failure()
    {
        using var context = new Context();
        Session session = Session.For(context.Live, Channel, context.Root);
        Directory.CreateDirectory(session.Directory);
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, SessionStore.ManifestFileName), "not json");

        await context.Coordinator.PublishAsync(new DvrEvent.SessionRecovered(
            new Resumable(session, 2, Complete: true)));
        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);

        Assert.Equal("open failed", snapshot.LastDetail);
        Assert.False(string.IsNullOrEmpty(snapshot.LastError));
        Assert.False(context.Recorder.Started.TryRead(out _));

        await context.StopAsync();
    }

    [Fact]
    public async Task Retry_timers_use_the_injected_jitter_and_restart_the_recorder()
    {
        using var context = new Context();
        int run = 0;
        var secondStarted = new TaskCompletionSource<RecorderCall>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        context.Recorder.OnRun = async (call, stopRequested) =>
        {
            if (Interlocked.Increment(ref run) == 1)
            {
                await call.PublishAsync(new DvrEvent.RecorderExited(
                    call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Exited, "no media"));
                return;
            }

            secondStarted.TrySetResult(call);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopRequested);
            }
            catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
            {
            }

            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Stopped, "stopped"));
        };

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            await context.FixedJitter.Applied.WaitAsync(TimeSpan.FromSeconds(5)));

        TimeSpan dueTime;
        do
        {
            dueTime = await context.Clock.CreatedTimers.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        while (dueTime != TimeSpan.FromSeconds(1));

        context.Clock.Advance(dueTime);

        RecorderCall second = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, second.Job.Part);

        await context.Coordinator.PublishAsync(new DvrEvent.StopRequested());
        await context.StopAsync();
    }

    [Fact]
    public async Task The_queue_accepts_a_thousand_events_and_late_publishes_do_not_throw()
    {
        using var context = new Context();
        await RuntimeTestWait.ForStatusAsync(context.Status, Channel, _ => true);

        int applied = 0;
        context.Status.Changed += () => Interlocked.Increment(ref applied);

        for (int index = 0; index < 1000; index++)
        {
            await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
                new StreamObservation.Unknown("api failure"), context.Clock.GetUtcNow()));
        }

        await context.Coordinator.PublishAsync(new DvrEvent.StopRequested());
        await context.StopAsync();

        Assert.Equal(1001, Volatile.Read(ref applied));
        Assert.IsType<DvrState.Paused>(context.Status.Current[Channel].State);

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Unknown("late"), context.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task A_full_disk_skips_the_recorder_and_schedules_a_retry()
    {
        using var context = new Context(
            diskSpace: new FakeDiskSpace(0, DiskGuard.BytesPerGib * 4));

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);

        Assert.Contains("disk", snapshot.LastDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disk", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Recorder.Started.TryRead(out _));

        await context.StopAsync();
    }

    [Fact]
    public async Task A_disk_probe_failure_still_publishes_one_exit()
    {
        using var context = new Context(diskSpace: new ThrowingDiskSpace());

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);

        Assert.Contains("disk probe failed", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Recorder.Started.TryRead(out _));

        await context.StopAsync();
    }

    [Fact]
    public async Task A_failed_finalize_is_surfaced_and_leaves_the_session_recoverable()
    {
        using var context = new Context();
        context.Recorder.OnRun = async (call, _) =>
        {
            WritePart(call.Session, 1);
            await call.PublishAsync(new DvrEvent.PartProducingMedia(
                call.Job, context.Clock.GetUtcNow()));
            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Exited, "stream ended"));
        };

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));
        await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);

        // The atomic index write uses <index>.tmp; a directory there makes it fail.
        Directory.CreateDirectory(
            Path.Combine(context.SessionDirectory, SessionStore.IndexFileName + ".tmp"));

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Offline(), context.Clock.GetUtcNow()));
        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Offline(), context.Clock.GetUtcNow()));

        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => !string.IsNullOrEmpty(status.LastError));

        Assert.IsType<DvrState.Idle>(snapshot.State);
        SessionManifest? manifest = await context.Store.TryLoadAsync(
            context.SessionDirectory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Recording, manifest!.Status);
        Assert.False(File.Exists(
            Path.Combine(context.SessionDirectory, SessionStore.IndexFileName)));
        Assert.Contains(context.Logs.Messages,
            message => message.Contains("finalize failed", StringComparison.OrdinalIgnoreCase));

        await context.StopAsync();
    }

    [Fact]
    public async Task A_stop_request_cancels_a_pending_retry()
    {
        using var context = new Context();
        int runs = 0;
        context.Recorder.OnRun = async (call, _) =>
        {
            Interlocked.Increment(ref runs);
            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Exited, "no media"));
        };

        string sessionId = Path.GetFileName(context.SessionDirectory);
        var sessionRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Library.Changed += () =>
        {
            if (context.Library.Find(sessionId) is null)
                sessionRemoved.TrySetResult();
        };

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));
        await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.WaitingToRetry);
        await context.FixedJitter.Applied.WaitAsync(TimeSpan.FromSeconds(5));

        await context.Coordinator.PublishAsync(new DvrEvent.StopRequested());

        // Removing the empty session proves the finalize command ran, which is after the
        // stale retry timer was canceled.
        await sessionRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, context.Clock.ActiveTimers);
        Assert.Equal(1, runs);

        await context.StopAsync();
    }

    [Fact]
    public async Task A_job_that_will_not_stop_faults_the_channel_loop()
    {
        using var context = new Context();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Recorder.OnRun = async (_, _) => await release.Task;

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));
        await RuntimeTestWait.ForStatusAsync(
            context.Status, Channel, status => status.State is DvrState.Recording);

        await context.Coordinator.PublishAsync(new DvrEvent.StopRequested());

        try
        {
            TimeSpan dueTime;
            do
            {
                dueTime = await context.Clock.CreatedTimers.ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            while (dueTime < ChannelCoordinator.JobStopBudget);

            context.Clock.Advance(dueTime);

            await Assert.ThrowsAsync<ChannelFatalException>(
                async () => await context.Loop.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_retry_that_cannot_be_scheduled_faults_the_channel_loop()
    {
        using var context = new Context(jitter: new ThrowingRetryJitter());
        context.Recorder.OnRun = async (call, _) =>
            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Exited, "no media"));

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        await Assert.ThrowsAsync<ChannelFatalException>(
            async () => await context.Loop.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_retry_with_an_invalid_delay_faults_the_channel_loop()
    {
        using var context = new Context(jitter: new InvalidDelayRetryJitter());
        context.Recorder.OnRun = async (call, _) =>
            await call.PublishAsync(new DvrEvent.RecorderExited(
                call.Job, context.Clock.GetUtcNow(), RecorderExitKind.Exited, "no media"));

        await context.Coordinator.PublishAsync(new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(context.Live), context.Clock.GetUtcNow()));

        await Assert.ThrowsAsync<ChannelFatalException>(
            async () => await context.Loop.WaitAsync(TimeSpan.FromSeconds(5)));
    }


    private static void WritePart(Session session, int part)
    {
        Directory.CreateDirectory(session.Directory);
        File.WriteAllText(
            Path.Combine(session.Directory, $"p{part:D3}.m3u8"),
            $"#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n"
            + $"#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n"
            + $"#EXT-X-INDEPENDENT-SEGMENTS\n"
            + $"#EXT-X-MAP:URI=\"init_{part:D3}.mp4\"\n#EXTINF:2.0,\n"
            + $"p{part:D3}_00000.m4s\n");
        File.WriteAllBytes(Path.Combine(session.Directory, $"init_{part:D3}.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(session.Directory, $"p{part:D3}_00000.m4s"), [4, 5, 6]);
    }

    private sealed class Context : IDisposable
    {
        public static readonly DateTimeOffset StartedAt =
            new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

        public Context(IRetryJitter? jitter = null, IDiskSpace? diskSpace = null)
        {
            Root = Path.Combine(
                Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            Library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
            Store = new SessionStore(
                Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
                Library,
                Clock,
                NullLogger<SessionStore>.Instance);

            IDiskSpace disk = diskSpace
                ?? new FakeDiskSpace(DiskGuard.BytesPerGib * 2, DiskGuard.BytesPerGib * 4);
            var diskGuard = new DiskGuard(
                Options.Create(new StorageOptions { RecordingsRoot = Root, MinFreeGb = 1 }),
                disk);
            IRetryJitter coordinatorJitter = jitter ?? FixedJitter;
            Loggers = LoggerFactory.Create(builder => builder.AddProvider(Logs));

            Coordinator = new ChannelCoordinator(
                Channel,
                new DvrSettings(
                    Root,
                    Channel,
                    MaxRetries: 3,
                    RetryBase: TimeSpan.FromSeconds(2),
                    RetryMax: TimeSpan.FromSeconds(10),
                    FailuresBeforeCooldown: 3,
                    Cooldown: TimeSpan.FromMinutes(1),
                    HealthyPart: TimeSpan.FromSeconds(30)),
                new DvrState.Idle(),
                Recorder,
                Store,
                diskGuard,
                Status,
                coordinatorJitter,
                Clock,
                Loggers.CreateLogger($"StreamKeepr.Channel.{Channel}"));

            Loop = Coordinator.RunAsync(Cts.Token);
        }

        public string Root { get; }

        public TrackingTimeProvider Clock { get; } = new(StartedAt.AddMinutes(1));

        public RecordingLibrary Library { get; }

        public SessionStore Store { get; }

        public DvrStatus Status { get; } = new();

        public FakeRecorder Recorder { get; } = new();

        public FixedRetryJitter FixedJitter { get; } = new(TimeSpan.FromSeconds(1));

        public ListLoggerProvider Logs { get; } = new();

        private ILoggerFactory Loggers { get; }

        public ChannelCoordinator Coordinator { get; }

        public CancellationTokenSource Cts { get; } = new();

        public Task Loop { get; }

        public LiveStream Live { get; } =
            new("123456789", "A stream", "Just Chatting", StartedAt, null);

        public string SessionDirectory =>
            Path.Combine(Root, Channel, $"20261110T163000Z_{Live.StreamId}");

        public async Task StopAsync()
        {
            Coordinator.Complete();
            await Loop.WaitAsync(TimeSpan.FromSeconds(5));
            Cts.Cancel();
        }

        public void Dispose()
        {
            Cts.Cancel();
            try
            {
                Loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            Cts.Dispose();
            Loggers.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
