using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using CliWrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Telemetry;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Telemetry;

namespace StreamKeepr.Tests.Media;

public sealed class RecorderJobTests : IDisposable
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private static readonly DateTime ProcessStartedAt = StartedAt.UtcDateTime;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "streamkeepr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly TestTelemetry _telemetry = new();

    public RecorderJobTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _telemetry.Dispose();
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
        Assert.Contains("pid ", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("started ", exited.Detail, StringComparison.Ordinal);
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
        Session session = NewSession();
        var runner = new FakeProcessRunner(start =>
            throw new InvalidOperationException($"{start.Tool} must not start for an existing part"));

        string sentinel = Path.Combine(session.Directory, PartFiles.Init(1));
        await File.WriteAllTextAsync(sentinel, "do not touch");

        IReadOnlyList<DvrEvent> events = await RunAsync(
            session, FakeOptions(), processRunner: runner);

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("already exists", exited.Detail, StringComparison.Ordinal);

        Assert.Equal("do not touch", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Playlist(1))));
        Assert.False(Directory.EnumerateFiles(session.Directory, "p001*").Any());
    }

    [Fact]
    public async Task A_run_without_media_publishes_no_media_event_and_deletes_part_files()
    {
        Session session = NewSession();
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session, FakeOptions(), processRunner: tools.Runner);
        await tools.Started;

        // FFmpeg opens its outputs before any media arrives; the job must remove them.
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, PartFiles.Init(1)), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, PartFiles.Playlist(1)), "#EXTM3U\n");
        tools.Streamlink.WriteError("stream ended without media");
        tools.Streamlink.Exit();
        await WaitForCompletionAsync(run, "no-media part");

        IReadOnlyList<DvrEvent> recorded = ReadEvents(events);
        Assert.Empty(recorded.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(recorded.OfType<DvrEvent.RecorderExited>());
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
        // The recorder stops a fixture with Ctrl+C, which CliWrap can only deliver to a process
        // with an attachable console. A dotnet test host has none on Windows, so the bounded
        // stop escalates to a force kill there; Linux CI still covers the graceful path.
        Assert.Equal(
            OperatingSystem.IsWindows() ? RecorderExitKind.Killed : RecorderExitKind.Stopped,
            exited.Kind);
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
        Session session = NewSession();
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session, FakeOptions(), processRunner: tools.Runner);
        await tools.Started;

        tools.Ffmpeg.Exit();
        await WaitForCompletionAsync(run, "ffmpeg exits first");

        IReadOnlyList<DvrEvent> recorded = ReadEvents(events);
        Assert.Empty(recorded.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(recorded.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("ffmpeg exited first", exited.Detail, StringComparison.Ordinal);
        Assert.True(
            tools.Streamlink.Started.GracefulStop.IsCancellationRequested,
            "Streamlink must be stopped once FFmpeg is gone");
        Assert.False(tools.Streamlink.Started.ForceKill.IsCancellationRequested);
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "p001*"));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "init_001*"));
    }

    [Fact]
    public async Task A_broken_streamlink_fails_recording_with_a_clear_detail()
    {
        Session session = NewSession();
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session, FakeOptions(), processRunner: tools.Runner);
        await tools.Started;

        tools.Streamlink.WriteError("error: simulated streamlink failure");
        tools.Streamlink.Exit(1);
        await WaitForCompletionAsync(run, "broken streamlink");

        IReadOnlyList<DvrEvent> recorded = ReadEvents(events);
        Assert.Empty(recorded.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(recorded.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("simulated streamlink failure", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("streamlink", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("exit 1", exited.Detail, StringComparison.Ordinal);
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
        Session session = NewSession();
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        RecorderOptions options = FakeOptions();
        options.TwitchUserOAuthToken = token;
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session,
            options,
            logger: loggerFactory.CreateLogger<RecorderJob>(),
            processRunner: tools.Runner);
        await tools.Started;

        IReadOnlyList<string> arguments = tools.Streamlink.Started.Launch.Arguments;
        foreach (string argument in arguments)
            tools.Streamlink.WriteError($"arg={argument}");
        tools.Streamlink.Exit(1);
        await WaitForCompletionAsync(run, "token echo");

        Assert.Contains(
            arguments,
            argument => argument.Contains($"Authorization=OAuth {token}", StringComparison.Ordinal));
        var exited = Assert.Single(ReadEvents(events).OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);

        // The echoed header proves redaction replaced the token, not just omitted the argument.
        Assert.Contains("Authorization=OAuth ***", exited.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(token, exited.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join("\n", logs.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_and_finish_logs_carry_the_channel_session_and_job_scope()
    {
        Session session = NewSession();
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session,
            FakeOptions(),
            logger: loggerFactory.CreateLogger<RecorderJob>(),
            processRunner: tools.Runner);
        await tools.Started;

        WritePartMedia(session, "aaaa");
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(1));
        tools.Streamlink.Exit();
        await WaitForCompletionAsync(run, "logged part");

        Assert.Single(ReadEvents(events).OfType<DvrEvent.PartProducingMedia>());
        IReadOnlyList<CapturedLogEntry> entries = logs.Entries;
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.Equal("streamer", entry.Scopes["Channel"]);
            Assert.Equal(session.Id, entry.Scopes["SessionId"]);
            Assert.Equal($"{session.Id}/p001", entry.Scopes["Job"]);
        });

        CapturedLogEntry started = Assert.Single(entries, entry => entry.Message.Contains("started part", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Equal("streamer", started.Properties["Channel"]);
        Assert.Equal($"{session.Id}/p001", started.Properties["Job"]?.ToString());
        Assert.Equal(1, started.Properties["Part"]);

        CapturedLogEntry finished = Assert.Single(entries, entry => entry.Message.Contains("finished", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, finished.Level);
        Assert.Equal(RecorderExitKind.Exited, finished.Properties["Kind"]);
        Assert.IsType<double>(finished.Properties["ElapsedMs"]);
        long bytes = Assert.IsType<long>(finished.Properties["Bytes"]);
        Assert.True(bytes > 0, "a part with media must report its bytes");
        Assert.Equal(PartFiles.TotalBytes(session.Directory, 1), bytes);
        var order = entries.ToList();
        Assert.True(
            order.IndexOf(started) < order.IndexOf(finished),
            "the start log must precede the finish log");
    }

    [Fact]
    public async Task Hostile_tool_output_is_logged_only_at_debug_redacted_and_escaped()
    {
        const string token = "test-oauth-token-27-do-not-log";
        Session session = NewSession();
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        RecorderOptions options = FakeOptions();
        options.TwitchUserOAuthToken = token;
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session,
            options,
            logger: loggerFactory.CreateLogger<RecorderJob>(),
            processRunner: tools.Runner);
        await tools.Started;

        // The token, an ANSI escape, a U+2028 line separator, and a forged log prefix.
        tools.Streamlink.WriteError(
            $"hostile: token={token} \u001b[31mred\u001b[0m\u2028[ERR] forged");
        tools.Streamlink.Exit(1);
        await WaitForCompletionAsync(run, "hostile output");

        Assert.Single(ReadEvents(events).OfType<DvrEvent.RecorderExited>());
        IReadOnlyList<CapturedLogEntry> entries = logs.Entries;
        CapturedLogEntry raw = Assert.Single(entries, entry => entry.Message.Contains("hostile:", StringComparison.Ordinal)
            && entry.Message.StartsWith("[streamlink", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, raw.Level);
        Assert.Equal(
            "hostile: token=*** \\u001B[31mred\\u001B[0m\\u2028[ERR] forged",
            raw.Properties["Line"]);

        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                entry.Message,
                c => c < (char)0x20 || c == (char)0x7f || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029);
        });
    }

    [Fact]
    public async Task A_stalled_streamlink_is_stopped_by_the_watchdog_and_reported_killed()
    {
        Session session = NewSession();
        var clock = new ManualTimeProvider(StartedAt);
        RecorderOptions options = FakeOptions();
        options.StallTimeoutSeconds = 15;
        using var stalls = new MetricCollector<long>(
            _telemetry.MeterFactory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.stalls");
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session, options, clock, processRunner: tools.Runner);
        await tools.Started;
        await WaitForAsync(() => clock.TimerCount > 0, "the stall watchdog");

        WritePartMedia(session, "aaaa");
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(1));

        // Streamlink keeps its output open but delivers nothing more.
        clock.Advance(TimeSpan.FromSeconds(16), autoFireOneShot: true);
        await WaitForCompletionAsync(run, "stall watchdog");

        IReadOnlyList<DvrEvent> recorded = ReadEvents(events);
        var media = Assert.Single(recorded.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(recorded.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Killed, exited.Kind);
        Assert.Contains("stall", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("no new media", exited.Detail, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(stalls.GetMeasurementSnapshot()).Value);
        Assert.True(
            recorded.ToList().IndexOf(media) < recorded.ToList().IndexOf(exited),
            "PartProducingMedia must be published before RecorderExited");

        // The stall still uses the bounded shutdown: a graceful Streamlink stop, then stdin EOF
        // so FFmpeg writes its trailer instead of being killed.
        Assert.True(tools.Streamlink.Started.GracefulStop.IsCancellationRequested);
        Assert.False(tools.Streamlink.Started.ForceKill.IsCancellationRequested);
        Assert.False(tools.Ffmpeg.Started.ForceKill.IsCancellationRequested);
    }

    [Fact]
    public async Task An_ad_break_grants_grace_and_does_not_trip_the_stall_watchdog()
    {
        Session session = NewSession();
        var clock = new ManualTimeProvider(StartedAt);
        RecorderOptions options = FakeOptions();
        options.StallTimeoutSeconds = 15;
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session, options, clock, processRunner: tools.Runner);
        await tools.Started;
        await WaitForAsync(() => clock.TimerCount > 0, "the stall watchdog");

        tools.Streamlink.WriteError("Detected advertisement break of 10 seconds");

        // The same jump kills the run without an ad announcement. Here the announced break plus
        // its safety margin pushes the watchdog deadline past this point.
        clock.Advance(TimeSpan.FromSeconds(16));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(run.IsCompleted, "the stall watchdog fired during an announced ad break");

        WritePartMedia(session, "aaaa");
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(1));
        tools.Streamlink.Exit();
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
        using var forceKills = new MetricCollector<long>(
            _telemetry.MeterFactory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.force_kills");
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
        Assert.Equal(1, Assert.Single(forceKills.GetMeasurementSnapshot()).Value);

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

    [Fact]
    public async Task Progress_is_nondecreasing_and_the_final_sample_matches_the_part_bytes()
    {
        Session session = NewSession();
        var progress = new List<(long Bytes, TimeSpan MediaTime)>();
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session,
            FakeOptions(),
            reportProgress: (_, bytes, mediaTime) =>
            {
                lock (progress)
                    progress.Add((bytes, mediaTime));
            },
            processRunner: tools.Runner);
        await tools.Started;

        WritePartMedia(session, "aaaa", "bbbbbb");
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(1));
        WritePartMedia(session, "aaaa", "bbbbbb", "cc");
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(2));
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(2), end: true);
        tools.Ffmpeg.Exit();
        await WaitForCompletionAsync(run, "progress part");

        var exited = Assert.Single(ReadEvents(events).OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);

        List<(long Bytes, TimeSpan MediaTime)> samples;
        lock (progress)
            samples = progress.ToList();

        Assert.True(samples.Count >= 2, "expected a progress sample before the final block");
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(
                samples[i].Bytes >= samples[i - 1].Bytes,
                $"part bytes went backwards: {samples[i - 1].Bytes} then {samples[i].Bytes}");
        }

        long onDisk = PartFiles.TotalBytes(session.Directory, 1);
        Assert.Equal(16, onDisk);
        Assert.Equal(onDisk, samples[^1].Bytes);
    }

    [Fact]
    public async Task An_unreadable_playlist_does_not_fail_the_part()
    {
        Session session = NewSession();
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        var progress = new List<(long Bytes, TimeSpan MediaTime)>();
        var tools = new FakeTools();
        (Task run, List<DvrEvent> events) = StartJob(
            session,
            FakeOptions(),
            logger: loggerFactory.CreateLogger<RecorderJob>(),
            reportProgress: (_, bytes, mediaTime) =>
            {
                lock (progress)
                    progress.Add((bytes, mediaTime));
            },
            processRunner: tools.Runner);
        await tools.Started;

        WritePartMedia(session, "aaaa");
        await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(1));

        string playlistPath = Path.Combine(session.Directory, PartFiles.Playlist(1));
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "p001_00001.m4s"), "bb");
        await File.AppendAllTextAsync(playlistPath, "p001_00001.m4s\n");

        // The progress callback runs on this thread, so the tracker reads while the lock is held.
        await using (var locked = new FileStream(
            playlistPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(2));
            await tools.Ffmpeg.ReportProgressAsync(TimeSpan.FromSeconds(2), end: true);
            tools.Ffmpeg.Exit();
            await WaitForCompletionAsync(run, "recorder job");
        }

        var exited = Assert.Single(ReadEvents(events).OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);

        List<(long Bytes, TimeSpan MediaTime)> samples;
        lock (progress)
            samples = progress.ToList();
        Assert.Contains(samples, sample => sample.MediaTime == TimeSpan.FromSeconds(2));

        // Live progress keeps the last readable total; the part-end scan is authoritative.
        Assert.Equal(samples[0].Bytes, samples[^1].Bytes);

        long onDisk = PartFiles.TotalBytes(session.Directory, 1);
        Assert.True(onDisk > samples[^1].Bytes, "the locked playlist must hide the new segment");
        CapturedLogEntry finished = Assert.Single(
            logs.Entries,
            entry => entry.Message.Contains("finished", StringComparison.Ordinal));
        Assert.Equal(onDisk, Assert.IsType<long>(finished.Properties["Bytes"]));
    }

    [Fact]
    public async Task An_unverified_streamlink_shutdown_raises_a_shutdown_exception()
    {
        DateTime captured = ProcessStartedAt;
        var ffmpeg = new TaskCompletionSource<ProcessOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlink = new TaskCompletionSource<ProcessOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;

        var runner = new FakeProcessRunner((tool, _, _) =>
        {
            if (Interlocked.Increment(ref starts) == 2)
                started.TrySetResult();

            return tool == "ffmpeg"
                ? new RunningProcess(4322, captured, ffmpeg.Task)
                : new RunningProcess(4321, captured, streamlink.Task);
        });

        using var stop = new CancellationTokenSource();
        (Task run, _) = StartJob(
            NewSession(),
            FakeOptions(),
            new ScaledTimeProvider(),
            stopRequested: stop.Token,
            processRunner: runner,
            identityProbe: (pid, at) => ProcessIdentityInspector.Inspect(pid, at, _ => captured));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();

        RecorderShutdownException error = await Assert.ThrowsAsync<RecorderShutdownException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("streamlink", error.Tool);
        Assert.Equal(4321, error.ProcessId);
        Assert.Equal(captured, error.CapturedStartTime);
        Assert.Equal(ProcessStop.TimedOut, error.Stop);
        Assert.True(error.TaskPending);
        Assert.Contains("still alive", error.ObservedState, StringComparison.Ordinal);
        Assert.Contains("streamlink", error.Message, StringComparison.Ordinal);
        Assert.Contains("4321", error.Message, StringComparison.Ordinal);
        Assert.Contains("task pending", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verified_force_kill_stays_killed()
    {
        DateTime captured = ProcessStartedAt;
        var streamlink = new TaskCompletionSource<ProcessOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner((tool, forceKill, _) =>
        {
            if (tool == "ffmpeg")
            {
                return new RunningProcess(
                    4322, captured, Task.FromResult(Completed(4322, captured)));
            }

            forceKill.Register(() => streamlink.TrySetResult(
                new ProcessOutcome(0, null, true, 4321, captured, ProcessStop.AlreadyStopped, false)));
            return new RunningProcess(4321, captured, streamlink.Task);
        });

        IReadOnlyList<DvrEvent> events = await RunAsync(
            NewSession(),
            FakeOptions(),
            clock: new ScaledTimeProvider(),
            processRunner: runner,
            identityProbe: (pid, at) =>
                ProcessIdentityInspector.Inspect(pid, at, _ => throw new ArgumentException("gone")));

        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Killed, exited.Kind);
        Assert.Contains("streamlink", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("4321", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("stop Forced", exited.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_stream_end_stays_exited()
    {
        DateTime captured = ProcessStartedAt;
        var runner = new FakeProcessRunner((tool, _, _) =>
            tool == "ffmpeg"
                ? new RunningProcess(4322, captured, Task.FromResult(Completed(4322, captured)))
                : new RunningProcess(4321, captured, Task.FromResult(Completed(4321, captured))));

        IReadOnlyList<DvrEvent> events = await RunAsync(
            NewSession(),
            FakeOptions(),
            clock: new ScaledTimeProvider(),
            processRunner: runner,
            identityProbe: (_, _) => throw new InvalidOperationException(
                "a clean exit must not inspect process identity"));

        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("streamlink", exited.Detail, StringComparison.Ordinal);
        Assert.Contains("exit 0", exited.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_force_killed_pid_that_is_still_alive_fails_closed()
    {
        DateTime captured = ProcessStartedAt;
        var streamlink = new TaskCompletionSource<ProcessOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner((tool, forceKill, _) =>
        {
            if (tool == "ffmpeg")
            {
                return new RunningProcess(
                    4322, captured, Task.FromResult(Completed(4322, captured)));
            }

            forceKill.Register(() => streamlink.TrySetResult(
                new ProcessOutcome(0, null, true, 4321, captured, ProcessStop.AlreadyStopped, false)));
            return new RunningProcess(4321, captured, streamlink.Task);
        });

        RecorderShutdownException error = await Assert.ThrowsAsync<RecorderShutdownException>(
            () => RunAsync(
                NewSession(),
                FakeOptions(),
                clock: new ScaledTimeProvider(),
                processRunner: runner,
                identityProbe: (pid, at) =>
                    ProcessIdentityInspector.Inspect(pid, at, _ => captured)));

        Assert.Equal(ProcessStop.Unverified, error.Stop);
        Assert.False(error.TaskPending);
        Assert.Contains("still alive", error.ObservedState, StringComparison.Ordinal);
        Assert.Contains("4321", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_force_killed_pid_with_an_unreadable_identity_fails_closed()
    {
        DateTime captured = ProcessStartedAt;
        var streamlink = new TaskCompletionSource<ProcessOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner((tool, forceKill, _) =>
        {
            if (tool == "ffmpeg")
            {
                return new RunningProcess(
                    4322, captured, Task.FromResult(Completed(4322, captured)));
            }

            forceKill.Register(() => streamlink.TrySetResult(
                new ProcessOutcome(0, null, true, 4321, captured, ProcessStop.AlreadyStopped, false)));
            return new RunningProcess(4321, captured, streamlink.Task);
        });

        RecorderShutdownException error = await Assert.ThrowsAsync<RecorderShutdownException>(
            () => RunAsync(
                NewSession(),
                FakeOptions(),
                clock: new ScaledTimeProvider(),
                processRunner: runner,
                identityProbe: (pid, at) => ProcessIdentityInspector.Inspect(
                    pid, at, _ => throw new UnauthorizedAccessException("denied"))));

        Assert.Equal(ProcessStop.Unverified, error.Stop);
        Assert.Contains("could not be read", error.ObservedState, StringComparison.Ordinal);
        Assert.Contains("4321", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unverified_shutdown_still_counts_the_part_and_logs_its_finish()
    {
        using var parts = new MetricCollector<long>(
            _telemetry.MeterFactory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");
        using var forceKills = new MetricCollector<long>(
            _telemetry.MeterFactory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.force_kills");
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        DateTime captured = ProcessStartedAt;
        var streamlink = new TaskCompletionSource<ProcessOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner((tool, forceKill, _) =>
        {
            if (tool == "ffmpeg")
            {
                return new RunningProcess(
                    4322, captured, Task.FromResult(Completed(4322, captured)));
            }

            forceKill.Register(() => streamlink.TrySetResult(
                new ProcessOutcome(0, null, true, 4321, captured, ProcessStop.AlreadyStopped, false)));
            return new RunningProcess(4321, captured, streamlink.Task);
        });

        await Assert.ThrowsAsync<RecorderShutdownException>(
            () => RunAsync(
                NewSession(),
                FakeOptions(),
                clock: new ScaledTimeProvider(),
                logger: loggerFactory.CreateLogger<RecorderJob>(),
                processRunner: runner,
                identityProbe: (pid, at) =>
                    ProcessIdentityInspector.Inspect(pid, at, _ => captured)));

        CollectedMeasurement<long> part = Assert.Single(parts.GetMeasurementSnapshot());
        Assert.Equal("killed", part.Tags[StreamKeeprTelemetry.RecorderExitKindAttribute]);
        Assert.Equal(1, Assert.Single(forceKills.GetMeasurementSnapshot()).Value);

        CapturedLogEntry finished = Assert.Single(
            logs.Entries,
            entry => entry.Message.Contains("finished", StringComparison.Ordinal));
        Assert.Equal(RecorderExitKind.Killed, finished.Properties["Kind"]);
        Assert.Contains("4321", finished.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_whose_size_cannot_be_read_is_counted_without_a_size_sample()
    {
        using var parts = new MetricCollector<long>(
            _telemetry.MeterFactory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");
        using var sizes = new MetricCollector<long>(
            _telemetry.MeterFactory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.part.size");
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        DateTime captured = ProcessStartedAt;
        var runner = new FakeProcessRunner((tool, _, _) =>
            tool == "ffmpeg"
                ? new RunningProcess(4322, captured, Task.FromResult(Completed(4322, captured)))
                : new RunningProcess(4321, captured, Task.FromResult(Completed(4321, captured))));

        IReadOnlyList<DvrEvent> events = await RunAsync(
            NewSession(),
            FakeOptions(),
            clock: new ScaledTimeProvider(),
            logger: loggerFactory.CreateLogger<RecorderJob>(),
            processRunner: runner,
            partBytesScan: (_, _) => throw new IOException("part directory unreadable"));

        Assert.Equal(
            RecorderExitKind.Exited,
            Assert.Single(events.OfType<DvrEvent.RecorderExited>()).Kind);
        Assert.Single(parts.GetMeasurementSnapshot());
        Assert.Empty(sizes.GetMeasurementSnapshot());

        CapturedLogEntry finished = Assert.Single(
            logs.Entries,
            entry => entry.Message.Contains("finished", StringComparison.Ordinal));
        Assert.Null(finished.Properties["Bytes"]);
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("part size", StringComparison.Ordinal));
    }

    private static ProcessOutcome Completed(int pid, DateTime started, int exitCode = 0) =>
        new(exitCode, null, false, pid, started, ProcessStop.AlreadyStopped, false);

    private static RecorderOptions FakeOptions() => new()
    {
        StreamlinkPath = "streamlink",
        FfmpegPath = "ffmpeg",
        Quality = "best",
        SegmentSeconds = 2,
        FinalizeTimeoutSeconds = 10,
    };

    /// <summary>One fake tool start: the launch, its output callbacks, and its stop tokens.</summary>
    private sealed record FakeStart(
        ToolLaunch Launch,
        Func<string, ValueTask>? Stdout,
        Action<string> Stderr,
        CancellationToken ForceKill,
        CancellationToken GracefulStop)
    {
        public string Tool => Launch.Tool;
    }

    private sealed class FakeProcessRunner(Func<FakeStart, RunningProcess> start)
        : IRecorderProcessRunner
    {
        public FakeProcessRunner(
            Func<string, CancellationToken, CancellationToken, RunningProcess> start)
            : this(fake => start(fake.Tool, fake.ForceKill, fake.GracefulStop))
        {
        }

        public RunningProcess Start(
            ToolLaunch launch,
            Func<string, ValueTask>? onStdoutLine,
            Action<string> onStderrLine,
            CancellationToken forceKill,
            CancellationToken gracefulStop) =>
            start(new FakeStart(launch, onStdoutLine, onStderrLine, forceKill, gracefulStop));
    }

    /// <summary>
    /// A scripted tool behind the fake runner. The test writes its output lines and decides when
    /// it exits; a force kill always ends it, like a real process.
    /// </summary>
    private sealed class FakeTool(int processId)
    {
        private readonly TaskCompletionSource<ProcessOutcome> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FakeStart? _start;
        private ExecutionContext? _context;

        public FakeStart Started =>
            Volatile.Read(ref _start) ?? throw new InvalidOperationException("the tool has not started");

        public RunningProcess Launch(FakeStart start, bool exitOnGracefulStop, bool exitOnInputEof)
        {
            // CliWrap runs output callbacks in the context that started the process, so the
            // job's logging scope applies to them; the fake must do the same.
            _context = ExecutionContext.Capture();
            Volatile.Write(ref _start, start);
            start.ForceKill.Register(() => Exit(cancelled: true));
            if (exitOnGracefulStop)
                start.GracefulStop.Register(() => Exit(cancelled: true));
            if (exitOnInputEof && start.Launch.StandardInput is { } input)
            {
                // FFmpeg's graceful finish: it writes its trailer once stdin reaches EOF.
                _ = Task.Run(async () =>
                {
                    await input.CopyToAsync(Stream.Null);
                    Exit();
                });
            }

            return new RunningProcess(processId, ProcessStartedAt, _exit.Task);
        }

        public ValueTask WriteLineAsync(string line)
        {
            Func<string, ValueTask> stdout =
                Started.Stdout ?? throw new InvalidOperationException("stdout is a byte stream");
            ValueTask written = default;
            ExecutionContext.Run(_context!, _ => written = stdout(line), null);
            return written;
        }

        public void WriteError(string line)
        {
            Action<string> stderr = Started.Stderr;
            ExecutionContext.Run(_context!, _ => stderr(line), null);
        }

        /// <summary>Writes one FFmpeg <c>-progress</c> block.</summary>
        public async Task ReportProgressAsync(TimeSpan mediaTime, bool end = false)
        {
            await WriteLineAsync("total_size=N/A");
            await WriteLineAsync($"out_time_us={(long)mediaTime.TotalMicroseconds}");
            await WriteLineAsync(end ? "progress=end" : "progress=continue");
        }

        public void Exit(int exitCode = 0, bool cancelled = false) =>
            _exit.TrySetResult(new ProcessOutcome(
                cancelled ? null : exitCode,
                null,
                cancelled,
                processId,
                ProcessStartedAt,
                ProcessStop.AlreadyStopped,
                false));
    }

    /// <summary>
    /// Streamlink and FFmpeg behind one fake runner. By default Streamlink exits on its graceful
    /// signal and FFmpeg exits when its stdin reaches EOF, as the real tools do.
    /// </summary>
    private sealed class FakeTools
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _starts;

        public FakeTools()
        {
            Runner = new FakeProcessRunner(start =>
            {
                RunningProcess process = start.Tool == "ffmpeg"
                    ? Ffmpeg.Launch(start, exitOnGracefulStop: false, exitOnInputEof: true)
                    : Streamlink.Launch(start, exitOnGracefulStop: true, exitOnInputEof: false);
                if (Interlocked.Increment(ref _starts) == 2)
                    _started.TrySetResult();
                return process;
            });
        }

        public FakeTool Streamlink { get; } = new(4321);

        public FakeTool Ffmpeg { get; } = new(4322);

        public FakeProcessRunner Runner { get; }

        public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Writes what FFmpeg leaves for part 1: init, these segments, and the EVENT playlist.</summary>
    private static void WritePartMedia(Session session, params string[] segments)
    {
        File.WriteAllText(Path.Combine(session.Directory, PartFiles.Init(1)), "init");
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\n");
        for (int i = 0; i < segments.Length; i++)
        {
            string name = $"p001_{i:D5}.m4s";
            File.WriteAllText(Path.Combine(session.Directory, name), segments[i]);
            playlist.Append(name).Append('\n');
        }

        File.WriteAllText(
            Path.Combine(session.Directory, PartFiles.Playlist(1)), playlist.ToString());
    }

    private (Task Run, List<DvrEvent> Events) StartJob(
        Session session,
        RecorderOptions options,
        TimeProvider? clock = null,
        ILogger<RecorderJob>? logger = null,
        CancellationToken stopRequested = default,
        Action<JobId, long, TimeSpan>? reportProgress = null,
        IRecorderProcessRunner? processRunner = null,
        Func<int?, DateTime?, ProcessIdentity>? identityProbe = null,
        Func<string, int, long>? partBytesScan = null)
    {
        var events = new List<DvrEvent>();
        ValueTask Publish(DvrEvent dvrEvent, CancellationToken _)
        {
            lock (events)
                events.Add(dvrEvent);
            return ValueTask.CompletedTask;
        }

        IOptions<RecorderOptions> configured = Options.Create(options);
        TimeProvider time = clock ?? TimeProvider.System;
        ILogger<RecorderJob> recorderLogger = logger ?? NullLogger<RecorderJob>.Instance;
        var job = processRunner is null
            ? new RecorderJob(configured, time, recorderLogger, _telemetry.Value)
            : new RecorderJob(
                configured,
                time,
                recorderLogger,
                _telemetry.Value,
                processRunner,
                identityProbe,
                partBytesScan);
        Task run = job.RunAsync(
            session, new JobId(session.Id, 1), Publish, stopRequested, reportProgress);
        return (run, events);
    }

    private async Task<IReadOnlyList<DvrEvent>> RunAsync(
        Session session,
        RecorderOptions options,
        CancellationToken stopRequested = default,
        TimeProvider? clock = null,
        ILogger<RecorderJob>? logger = null,
        Action<JobId, long, TimeSpan>? reportProgress = null,
        IRecorderProcessRunner? processRunner = null,
        Func<int?, DateTime?, ProcessIdentity>? identityProbe = null,
        Func<string, int, long>? partBytesScan = null)
    {
        (Task run, List<DvrEvent> events) = StartJob(
            session,
            options,
            clock,
            logger,
            stopRequested,
            reportProgress,
            processRunner,
            identityProbe,
            partBytesScan);
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

        /// <summary>Timers created and not yet disposed, so a test can wait for the watchdog.</summary>
        public int TimerCount
        {
            get
            {
                lock (_gate)
                    return _timers.Count;
            }
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
}
