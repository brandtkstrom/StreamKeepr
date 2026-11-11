using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using CliWrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class RecorderJobTests : IDisposable
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "streamkeepr-tests",
        Guid.NewGuid().ToString("N"));

    public RecorderJobTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Records_one_complete_part_from_a_fake_streamlink()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        var media = Assert.Single(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(2, events.Count);

        var job = new JobId(session.Id, 1);
        Assert.Equal(job, media.Job);
        Assert.Equal(job, exited.Job);
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.True(
            events.ToList().IndexOf(media) < events.ToList().IndexOf(exited),
            "PartProducingMedia must be published before RecorderExited");

        string playlistPath = Path.Combine(session.Directory, PartFiles.Playlist(1));
        string initPath = Path.Combine(session.Directory, PartFiles.Init(1));
        Assert.True(File.Exists(playlistPath), $"missing part playlist at {playlistPath}");
        Assert.True(File.Exists(initPath), $"missing init segment at {initPath}");
        Assert.True(new FileInfo(initPath).Length > 0, "init segment is empty");

        string playlist = await File.ReadAllTextAsync(playlistPath);
        Assert.StartsWith("#EXTM3U", playlist);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", playlist);
        Assert.Contains("#EXT-X-INDEPENDENT-SEGMENTS", playlist);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:", playlist);
        Assert.Contains("#EXT-X-MAP:URI=\"init_001.mp4\"", playlist);
        Assert.Contains("#EXT-X-ENDLIST", playlist);

        string[] segments = Directory.GetFiles(session.Directory, "p001_*.m4s");
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.True(new FileInfo(segment).Length > 0));
        Assert.Contains(Path.GetFileName(segments[0]), playlist);
        Assert.Empty(Directory.GetFiles(session.Directory, "*.tmp"));

        await AssertPlayableAsync(ffmpeg, playlistPath);
    }

    [Fact]
    public async Task Existing_part_is_reported_without_starting_processes_or_overwriting_any_file()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-hanging-streamlink");
        options.FfmpegPath = FixturePath("fake-hanging-ffmpeg");

        string sentinel = Path.Combine(session.Directory, PartFiles.Init(1));
        await File.WriteAllTextAsync(sentinel, "do not touch");

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("already exists", exited.Detail, StringComparison.Ordinal);

        Assert.Equal("do not touch", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Playlist(1))));
        Assert.False(Directory.EnumerateFiles(session.Directory, "p001*").Any());
        Assert.False(
            File.Exists(Path.Combine(session.Directory, "streamlink.started")),
            "Streamlink must not start for an existing part");
        Assert.False(
            File.Exists(Path.Combine(session.Directory, "ffmpeg.started")),
            "FFmpeg must not start for an existing part");
    }

    [Fact]
    public async Task A_run_without_media_publishes_no_media_event_and_deletes_part_files()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-streamlink-no-media");

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("without media", exited.Detail, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Playlist(1))));
        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Init(1))));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "p001*"));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "init_001*"));
    }

    [Fact]
    public async Task A_requested_stop_finalizes_the_part_and_reports_stopped()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-live-streamlink");

        using var stop = new CancellationTokenSource();
        Task stopper = Task.Run(async () =>
        {
            // Cancel while the live stream is still producing, so the part really is finalized
            // by EOF rather than by a natural stream end.
            await WaitForPartSegmentAsync(session.Directory, part: 1);
            stop.Cancel();
        });

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options, stop.Token);
        await stopper;

        var media = Assert.Single(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Stopped, exited.Kind);
        Assert.True(
            events.ToList().IndexOf(media) < events.ToList().IndexOf(exited),
            "PartProducingMedia must be published before RecorderExited");

        string playlistPath = Path.Combine(session.Directory, PartFiles.Playlist(1));
        Assert.True(File.Exists(playlistPath), $"missing part playlist at {playlistPath}");

        string playlist = await File.ReadAllTextAsync(playlistPath);
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        await AssertPlayableAsync(ffmpeg, playlistPath);
    }

    [Fact]
    public async Task Ffmpeg_exiting_first_stops_streamlink_and_reports_exited()
    {
        string trueCommand = FindExecutable("true")
            ?? throw new InvalidOperationException("the true command is required by this test");
        Session session = NewSession();
        RecorderOptions options = NewOptions(trueCommand);

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("ffmpeg exited first", exited.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "p001*"));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "init_001*"));
    }

    [Fact]
    public async Task A_broken_streamlink_fails_recording_with_a_clear_detail()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-streamlink-broken");

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("simulated streamlink failure", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("streamlink", exited.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "p001*"));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "init_001*"));
    }

    [Fact]
    public async Task The_broken_fixture_answers_a_version_preflight()
    {
        // Preflight probes --version before recording; the broken fixture must pass it and only
        // fail once it is asked to stream.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(FixturePath("fake-streamlink-broken"))
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("--version");

        Assert.True(process.Start(), "the broken fixture failed to start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string output = await stdout;
        await stderr;

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("streamlink", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subscriber_token_is_sent_to_streamlink_but_never_logged_or_reported()
    {
        const string token = "test-oauth-token-27-do-not-log";
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        var logger = new RecorderLogger();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-streamlink-token-echo");
        options.TwitchUserOAuthToken = token;

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options, logger: logger);

        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);

        // The fake echoes its arguments, so this proves the header reached Streamlink while the
        // token itself was replaced in the diagnostic detail.
        Assert.Contains("Authorization=OAuth ***", exited.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(token, exited.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join("\n", logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stalled_streamlink_is_stopped_by_the_watchdog_and_reported_killed()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        var clock = new ManualTimeProvider(StartedAt);
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-streamlink-stall");
        options.StallTimeoutSeconds = 15;

        using var safetyStop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (Task run, List<DvrEvent> events) = StartJob(
            session, options, clock, stopRequested: safetyStop.Token);

        await WaitForPartSegmentAsync(session.Directory, part: 1);
        // Fire the watchdog; subsequent shutdown waits then fall back to short real timers so a
        // stuck test process cannot hang the suite.
        clock.Advance(TimeSpan.FromSeconds(16), autoFireOneShot: true);
        await WaitForCompletionAsync(run, "stall watchdog");

        IReadOnlyList<DvrEvent> recorded = ReadEvents(events);
        var media = Assert.Single(recorded.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(recorded.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Killed, exited.Kind);
        Assert.Contains("stall", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("no new media", exited.Detail, StringComparison.Ordinal);
        Assert.True(
            recorded.ToList().IndexOf(media) < recorded.ToList().IndexOf(exited),
            "PartProducingMedia must be published before RecorderExited");

        string playlistPath = Path.Combine(session.Directory, PartFiles.Playlist(1));
        Assert.True(File.Exists(playlistPath), $"missing part playlist at {playlistPath}");
        Assert.Contains("#EXT-X-ENDLIST", await File.ReadAllTextAsync(playlistPath));
    }

    [Fact]
    public async Task An_ad_break_grants_grace_and_does_not_trip_the_stall_watchdog()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        var clock = new ManualTimeProvider(StartedAt);
        var logger = new RecorderLogger();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = FixturePath("fake-streamlink-adbreak");
        options.StallTimeoutSeconds = 15;

        using var safetyStop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (Task run, List<DvrEvent> events) = StartJob(
            session, options, clock, logger, safetyStop.Token);

        await logger.AdBreakSeen.WaitAsync(TimeSpan.FromSeconds(30));

        // The same jump kills the run without an ad announcement. Here the announced break plus
        // its safety margin pushes the watchdog deadline past this point.
        clock.Advance(TimeSpan.FromSeconds(16));
        await WaitForCompletionAsync(run, "ad-break grace");

        IReadOnlyList<DvrEvent> recorded = ReadEvents(events);
        Assert.Single(recorded.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(recorded.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.DoesNotContain("stall", exited.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bounded_pipe_backpressures_a_source_that_is_not_being_drained()
    {
        const long sourceBytes = 32L << 20;
        Pipe pipe = RecorderPipe.Create();

        Task<CommandResult> source = Cli.Wrap(FixturePath("fake-streamlink-slow"))
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()))
            .ExecuteAsync()
            .Task;

        // The source writes far more than the pipe can hold. Once it has filled the undrained
        // pipe it must stop; without backpressure this 32 MiB payload would already be done.
        await Task.Delay(TimeSpan.FromSeconds(1));
        ReadResult first = await pipe.Reader.ReadAsync();
        long buffered = first.Buffer.Length;
        Assert.False(source.IsCompleted, "the source finished while the pipe was not drained");
        Assert.InRange(
            buffered,
            RecorderPipe.PauseWriterThreshold,
            RecorderPipe.PauseWriterThreshold + (1 << 20));

        long total = 0;
        while (true)
        {
            total += first.Buffer.Length;
            pipe.Reader.AdvanceTo(first.Buffer.End);

            if (total >= sourceBytes)
                break;

            first = await pipe.Reader.ReadAsync();
        }

        Assert.Equal(sourceBytes, total);
        CommandResult completed = await source.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, completed.ExitCode);

        await pipe.Reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task A_worst_case_stop_fits_inside_the_35_second_job_budget()
    {
        Session session = NewSession();
        var clock = new ScaledTimeProvider();
        var options = new RecorderOptions
        {
            StreamlinkPath = FixturePath("fake-hanging-streamlink"),
            FfmpegPath = FixturePath("fake-hanging-ffmpeg"),
            Quality = "best",
            SegmentSeconds = 2,
            FinalizeTimeoutSeconds = 15,
        };

        using var stop = new CancellationTokenSource();
        Task stopper = Task.Run(async () =>
        {
            // Never stop before both fake processes are alive; otherwise the job takes the
            // pre-start shortcut and the shutdown path under test never runs.
            await WaitForFileAsync(Path.Combine(session.Directory, "streamlink.started"));
            await WaitForFileAsync(Path.Combine(session.Directory, "ffmpeg.started"));
            stop.Cancel();
        });

        long startedTicks = Stopwatch.GetTimestamp();
        IReadOnlyList<DvrEvent> events = await RunAsync(session, options, stop.Token, clock);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTicks);
        await stopper;

        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Killed, exited.Kind);

        // The fake processes never exit on their own and ignore SIGINT, so every wait in the
        // contract's shutdown order expires: 5 + 5 for Streamlink, 15 finalize for FFmpeg,
        // then 5 + 5. The injected clock records those durations and fires each wait after a
        // short real delay, so this runs in a few seconds instead of sleeping for 35 s.
        Assert.All(clock.RequestedWaits,
            wait => Assert.InRange(wait, TimeSpan.Zero, TimeSpan.FromSeconds(15)));
        Assert.Contains(TimeSpan.FromSeconds(15), clock.RequestedWaits);
        Assert.True(
            clock.RequestedWaits.Count(wait => wait == TimeSpan.FromSeconds(5)) >= 2,
            "both processes must have taken the graceful stop path");
        Assert.True(
            clock.RequestedWaits.Sum(wait => wait.TotalSeconds) <= 35,
            $"recorder requested {clock.RequestedWaits.Sum(wait => wait.TotalSeconds)} s of waits");
        Assert.True(elapsed < TimeSpan.FromSeconds(35), $"stop took {elapsed}");
    }

    private static (Task Run, List<DvrEvent> Events) StartJob(
        Session session,
        RecorderOptions options,
        TimeProvider? clock = null,
        ILogger<RecorderJob>? logger = null,
        CancellationToken stopRequested = default)
    {
        var events = new List<DvrEvent>();
        ValueTask Publish(DvrEvent dvrEvent, CancellationToken _)
        {
            lock (events)
                events.Add(dvrEvent);
            return ValueTask.CompletedTask;
        }

        var job = new RecorderJob(
            Options.Create(options),
            clock ?? TimeProvider.System,
            logger ?? NullLogger<RecorderJob>.Instance);
        Task run = job.RunAsync(session, new JobId(session.Id, 1), Publish, stopRequested);
        return (run, events);
    }

    private static async Task<IReadOnlyList<DvrEvent>> RunAsync(
        Session session,
        RecorderOptions options,
        CancellationToken stopRequested = default,
        TimeProvider? clock = null,
        ILogger<RecorderJob>? logger = null)
    {
        (Task run, List<DvrEvent> events) = StartJob(session, options, clock, logger, stopRequested);
        await WaitForCompletionAsync(run, "recorder job");
        return ReadEvents(events);
    }

    private static async Task WaitForCompletionAsync(Task run, string description)
    {
        Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.True(
            completed == run,
            $"recorder job did not finish within 60 seconds ({description})");
        await run;
    }

    private static IReadOnlyList<DvrEvent> ReadEvents(List<DvrEvent> events)
    {
        lock (events)
            return events.ToList();
    }

    private static async Task WaitForFileAsync(string path) =>
        await WaitForAsync(() => File.Exists(path), path);

    private static async Task WaitForPartSegmentAsync(string directory, int part) =>
        await WaitForAsync(
            () => Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, $"p{part:D3}_*.m4s").Any(),
            $"part {part:D3} media segments");

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException($"timed out waiting for {description}");
    }

    private RecorderOptions NewOptions(string ffmpeg) => new()
    {
        StreamlinkPath = FakeStreamlinkPath(),
        FfmpegPath = ffmpeg,
        Quality = "best",
        SegmentSeconds = 2,
        FinalizeTimeoutSeconds = 10,
    };

    private Session NewSession()
    {
        var live = new LiveStream(
            "123456789",
            "A test stream",
            "Just Chatting",
            StartedAt,
            ThumbnailUrl: null);
        Session session = Session.For(live, "streamer", _root);
        Directory.CreateDirectory(session.Directory);
        return session;
    }

    private static string RequireFfmpeg() => TestPaths.RequireExecutable(
        "ffmpeg",
        "ffmpeg is required for recorder integration tests. Install ffmpeg and put it on PATH.");

    private static string FakeStreamlinkPath() => FixturePath("fake-streamlink");

    private static string FixturePath(string name) => TestPaths.FixturePath(name);

    private static string? FindExecutable(string executable) => TestPaths.FindExecutable(executable);

    private static async Task AssertPlayableAsync(string ffmpeg, string playlistPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(playlistPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("null");
        process.StartInfo.ArgumentList.Add("-");

        Assert.True(process.Start(), "ffmpeg failed to start for playback verification");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string errors = await stderr;
        await stdout;

        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg could not play the recorded part (exit {process.ExitCode}): {errors}");
    }

    /// <summary>
    /// A clock that records each requested bounded wait but fires it after a short real delay, so
    /// a test can prove the 35 second shutdown budget without sleeping through it.
    /// </summary>
    private sealed class ScaledTimeProvider : TimeProvider
    {
        // Long enough for a force-killed process to exit under parallel test load.
        private static readonly TimeSpan RealDelay = TimeSpan.FromSeconds(1);

        private readonly object _gate = new();
        private readonly List<TimeSpan> _requestedWaits = [];

        public IReadOnlyList<TimeSpan> RequestedWaits
        {
            get
            {
                lock (_gate)
                    return _requestedWaits.ToArray();
            }
        }

        public override DateTimeOffset GetUtcNow() => StartedAt;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            // The watchdog's periodic five-second timer is not part of the shutdown budget.
            if (dueTime != Timeout.InfiniteTimeSpan && period == Timeout.InfiniteTimeSpan)
            {
                lock (_gate)
                    _requestedWaits.Add(dueTime);
            }

            var timer = new ScaledTimer(callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class ScaledTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return false;

                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(RealDelay);
                        if (Volatile.Read(ref _disposed) == 0)
                            callback(state);
                    });
                }

                return true;
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A clock that only moves when the test advances it. The watchdog timer is created with a
    /// five-second period, so the test can jump past the stall threshold at a deterministic point
    /// and then let the real processes finish their bounded shutdown.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;
        private bool _autoFireOneShot;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public void Advance(TimeSpan by, bool autoFireOneShot = false)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                _autoFireOneShot |= autoFireOneShot;
                due = [];
                foreach (ManualTimer timer in _timers.ToArray())
                {
                    if (!timer.IsDue(_now))
                        continue;

                    due.Add(timer);
                    timer.RescheduleAfterFire(_now);
                }
            }

            foreach (ManualTimer timer in due)
                timer.Fire();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Schedule(_now, dueTime, period);
            }

            return timer;
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private static readonly TimeSpan AutoFireDelay = TimeSpan.FromSeconds(2);

            private DateTimeOffset _due;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _scheduled;
            private CancellationTokenSource? _autoFire;
            private int _disposed;

            public bool IsDue(DateTimeOffset now) =>
                Volatile.Read(ref _disposed) == 0
                && _scheduled
                && _due <= now;

            public void RescheduleAfterFire(DateTimeOffset now)
            {
                if (_period == Timeout.InfiniteTimeSpan)
                    _scheduled = false;
                else
                    _due = now + _period;
            }

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                    callback(state);
            }

            public void Schedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _autoFire?.Cancel();
                _autoFire = null;

                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _scheduled = false;
                    return;
                }

                if (period == Timeout.InfiniteTimeSpan && owner._autoFireOneShot)
                {
                    // Bounded shutdown waits are one-shot. Once the test has fired the watchdog,
                    // let a genuinely stuck process take its timeout path instead of hanging the
                    // test forever; the real delay stays far above normal finalization time.
                    var autoFire = new CancellationTokenSource();
                    _autoFire = autoFire;
                    _scheduled = false;
                    _ = FireAfterDelayAsync(autoFire.Token);
                    return;
                }

                _due = now + dueTime;
                _scheduled = true;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        return false;

                    Schedule(owner._now, dueTime, period);
                    return true;
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _autoFire?.Cancel();
                    owner.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            private async Task FireAfterDelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await Task.Delay(AutoFireDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Fire();
            }
        }
    }

    /// <summary>Captures recorder logs and signals when Streamlink's ad-break line was handled.</summary>
    private sealed class RecorderLogger : ILogger<RecorderJob>
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly TaskCompletionSource _adBreakSeen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AdBreakSeen => _adBreakSeen.Task;

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            _messages.Enqueue(message);

            if (message.Contains("advertisement break", StringComparison.OrdinalIgnoreCase))
                _adBreakSeen.TrySetResult();
        }
    }
}
