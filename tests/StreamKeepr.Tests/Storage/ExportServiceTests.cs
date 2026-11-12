using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;
using CliWrap;
using Microsoft.Extensions.Time.Testing;

namespace StreamKeepr.Tests.Storage;

public sealed class ExportServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 10, 18, 0, 0, TimeSpan.Zero);
    private static readonly ClaimsPrincipal Admin =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mock_admin")], "test"));

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLibrary _library = new(NullLogger<RecordingLibrary>.Instance);
    private readonly ExportLeaseRegistry _leases = new();
    private readonly List<ExportService> _started = [];
    private ExportService _service = null!;
    private string _ffmpeg = null!;
    private string _ffprobe = null!;

    private string ExportsDirectory => AppDataPaths.ExportsDirectory(_root);

    public async Task InitializeAsync()
    {
        _ffmpeg = TestPaths.RequireExecutable(
            "ffmpeg", "ffmpeg is required for export tests. Install it and put it on PATH.");
        _ffprobe = TestPaths.RequireExecutable(
            "ffprobe", "ffprobe is required for export tests. Install it and put it on PATH.");
        _service = await StartServiceAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (ExportService service in _started)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<ExportService> StartServiceAsync(TimeProvider? clock = null, string? ffmpeg = null)
    {
        var service = new ExportService(
            _library,
            _leases,
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            Options.Create(new RecorderOptions { FfmpegPath = ffmpeg ?? _ffmpeg, FfprobePath = _ffprobe }),
            clock ?? TimeProvider.System,
            NullLogger<ExportService>.Instance);
        _started.Add(service);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    [Fact]
    public async Task Exports_a_finished_recording_to_one_faststart_mp4_holding_a_lease()
    {
        RecordingSummary summary = await SeedAsync("r1", "Night: chef special!");
        var progress = new List<double>();
        _service.Changed += () => progress.Add(_service.FindByRecording("r1")?.Progress ?? -1);

        ExportJob queued = _service.Start("r1", Admin, CancellationToken.None);
        Assert.Equal(ExportState.Queued, queued.State);
        Assert.True(_leases.IsLeased("r1"));

        ExportJob done = await WaitForAsync(queued.Id, ExportState.Succeeded);

        Assert.Equal(1, done.Progress);
        Assert.Equal("mock_nova_20261110-1800_Night-chef-special.mp4", done.FileName);
        Assert.False(_leases.IsLeased("r1"));
        string path = _service.FilePath(done.Id)!;
        Assert.Equal(Path.Combine(AppDataPaths.ExportsDirectory(_root), $"{done.Id}.mp4"), path);
        Assert.Empty(Directory.GetFiles(AppDataPaths.ExportsDirectory(_root), "*.tmp"));

        // faststart puts the moov atom before mdat.
        byte[] head = new byte[64 * 1024];
        await using (FileStream file = File.OpenRead(path))
            _ = await file.ReadAsync(head);
        string text = System.Text.Encoding.ASCII.GetString(head);
        Assert.True(text.IndexOf("moov", StringComparison.Ordinal) < text.IndexOf("mdat", StringComparison.Ordinal));
        Assert.Equal(1, progress[^1]);
        Assert.Same(_service.Find(done.Id), _service.FindByRecording(summary.Id));
    }

    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(15, 60, 0.25)]
    [InlineData(90, 60, 1)]
    [InlineData(10, 0, 0)]
    [InlineData(10, double.NaN, 0)]
    public void Progress_is_out_time_over_duration_clamped(double seconds, double duration, double expected)
    {
        Assert.Equal(expected, ExportService.ProgressFraction(TimeSpan.FromSeconds(seconds), duration));
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_in_order_and_a_second_start_returns_the_active_job()
    {
        await SeedAsync("r1", "First");
        await SeedAsync("r2", "Second");

        ExportJob first = _service.Start("r1", Admin, CancellationToken.None);
        ExportJob again = _service.Start("r1", Admin, CancellationToken.None);
        ExportJob second = _service.Start("r2", Admin, CancellationToken.None);
        Assert.Equal(first.Id, again.Id);

        bool overlapped = false;
        _service.Changed += () =>
        {
            if (_service.Find(first.Id)?.State == ExportState.Running
                && _service.Find(second.Id)?.State == ExportState.Running)
            {
                overlapped = true;
            }
        };

        await WaitForAsync(second.Id, ExportState.Succeeded);
        Assert.Equal(ExportState.Succeeded, _service.Find(first.Id)?.State);
        Assert.False(overlapped);
    }

    [Fact]
    public void Start_requires_an_admin_and_a_complete_known_recording()
    {
        _library.Upsert(Summary("live", recording: true));

        Assert.Throws<UnauthorizedAccessException>(() =>
            _service.Start("live", new ClaimsPrincipal(new ClaimsIdentity()), CancellationToken.None));
        Assert.Throws<KeyNotFoundException>(() => _service.Start("missing", Admin, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => _service.Start("live", Admin, CancellationToken.None));
        Assert.Null(_service.FindByRecording("live"));
        Assert.False(_leases.IsLeased("live"));
    }

    [Fact]
    public void Start_is_refused_while_a_delete_holds_the_recording()
    {
        _library.Upsert(Summary("delete-race"));

        IDisposable? held = _leases.TryBeginDelete("delete-race");
        Assert.NotNull(held);
        using (held)
            Assert.Throws<InvalidOperationException>(() =>
                _service.Start("delete-race", Admin, CancellationToken.None));

        Assert.Null(_service.FindByRecording("delete-race"));
        Assert.False(_leases.IsLeased("delete-race"));
    }

    [Fact]
    public async Task A_cleaned_up_file_is_no_longer_found_by_recording()
    {
        await SeedAsync("r1", "Title");
        ExportJob job = await WaitForAsync(_service.Start("r1", Admin, CancellationToken.None).Id, ExportState.Succeeded);

        File.Delete(_service.FilePath(job.Id)!);

        Assert.Null(_service.FindByRecording("r1"));
        Assert.Null(_service.FilePath(job.Id));
        Assert.NotNull(_service.Find(job.Id));
    }

    [Fact]
    public async Task A_missing_playlist_fails_the_job_with_a_safe_message_and_releases_the_lease()
    {
        _library.Upsert(Summary("gone"));

        ExportJob job = await WaitForAsync(_service.Start("gone", Admin, CancellationToken.None).Id, ExportState.Failed);

        Assert.Equal("The export could not be completed.", job.Error);
        Assert.False(_leases.IsLeased("gone"));
    }

    [Fact]
    public async Task A_failed_job_does_not_block_the_next_one()
    {
        // A channel that resolves outside the recordings root fails before FFmpeg runs.
        _library.Upsert(Summary("..") with { Channel = ".." });
        await SeedAsync("r2", "Next");

        ExportJob broken = _service.Start("..", Admin, CancellationToken.None);
        ExportJob next = _service.Start("r2", Admin, CancellationToken.None);

        await WaitForAsync(broken.Id, ExportState.Failed);
        await WaitForAsync(next.Id, ExportState.Succeeded);
    }

    [Fact]
    public async Task Incompatible_parts_fail_with_the_stable_code_and_produce_no_file()
    {
        await SeedPartsAsync("r1", ("160x90", "libx264"), ("320x180", "libx264"));

        ExportJob job = await WaitForAsync(_service.Start("r1", Admin, CancellationToken.None).Id, ExportState.Failed);

        Assert.Equal(ExportService.IncompatibleParts, job.Error);
        Assert.False(_leases.IsLeased("r1"));
        Assert.False(Directory.Exists(ExportsDirectory) && Directory.EnumerateFiles(ExportsDirectory).Any());
        Assert.Same(_service.Find(job.Id), _service.FindByRecording("r1"));
    }

    [Fact]
    public async Task Compatible_parts_export_to_one_mp4()
    {
        await SeedPartsAsync("r1", ("160x90", "libx264"), ("160x90", "libx264"));

        ExportJob job = await WaitForAsync(_service.Start("r1", Admin, CancellationToken.None).Id, ExportState.Succeeded);

        Assert.NotNull(_service.FilePath(job.Id));
    }

    [Fact]
    public async Task An_hevc_export_is_tagged_hvc1()
    {
        await SeedPartsAsync("r1", ("160x90", "libx265"));

        ExportJob job = await WaitForAsync(_service.Start("r1", Admin, CancellationToken.None).Id, ExportState.Succeeded);

        byte[] head = new byte[64 * 1024];
        await using (FileStream file = File.OpenRead(_service.FilePath(job.Id)!))
            _ = await file.ReadAsync(head);
        string text = System.Text.Encoding.ASCII.GetString(head);
        Assert.Contains("hvc1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hev1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_removes_it_from_the_queue_and_releases_its_lease()
    {
        await SeedAsync("r1", "First");
        await SeedAsync("r2", "Second");
        ExportJob first = _service.Start("r1", Admin, CancellationToken.None);
        ExportJob second = _service.Start("r2", Admin, CancellationToken.None);

        _service.Cancel(second.Id, Admin);

        Assert.Equal(ExportState.Cancelled, _service.Find(second.Id)?.State);
        Assert.False(_leases.IsLeased("r2"));
        await WaitForAsync(first.Id, ExportState.Succeeded);
        Assert.Equal(ExportState.Cancelled, _service.Find(second.Id)?.State);
        Assert.Null(_service.FilePath(second.Id));
        Assert.False(File.Exists(Path.Combine(ExportsDirectory, $"{second.Id}.mp4")));
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_ffmpeg_deletes_the_partial_file_and_allows_a_retry()
    {
        ExportService service = await StartServiceAsync(ffmpeg: TestPaths.FixturePath("fake-hanging-export-ffmpeg"));
        await SeedAsync("r1", "Title");
        ExportJob job = service.Start("r1", Admin, CancellationToken.None);
        string partial = Path.Combine(ExportsDirectory, $"{job.Id}.mp4.tmp");
        await WaitUntilAsync(() => service.Find(job.Id)?.State == ExportState.Running && File.Exists(partial));

        service.Cancel(job.Id, Admin);

        await WaitUntilAsync(() => service.Find(job.Id)?.State == ExportState.Cancelled);
        Assert.False(File.Exists(partial));
        Assert.False(_leases.IsLeased("r1"));
        Assert.Null(service.Find(job.Id)?.Error);

        ExportJob retry = service.Start("r1", Admin, CancellationToken.None);
        Assert.NotEqual(job.Id, retry.Id);
        Assert.Same(service.Find(retry.Id), service.FindByRecording("r1"));
    }

    [Fact]
    public async Task Cancel_requires_an_admin_a_known_job_and_an_active_one()
    {
        await SeedAsync("r1", "Title");
        ExportJob job = await WaitForAsync(_service.Start("r1", Admin, CancellationToken.None).Id, ExportState.Succeeded);

        Assert.Throws<UnauthorizedAccessException>(() =>
            _service.Cancel(job.Id, new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Throws<KeyNotFoundException>(() => _service.Cancel("missing", Admin));
        Assert.Throws<InvalidOperationException>(() => _service.Cancel(job.Id, Admin));
        Assert.Equal(ExportState.Succeeded, _service.Find(job.Id)?.State);
    }

    [Fact]
    public async Task Export_files_older_than_a_day_are_removed_at_startup_and_periodically()
    {
        Directory.CreateDirectory(ExportsDirectory);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        string stale = Path.Combine(ExportsDirectory, "stale.mp4");
        string staleTemp = Path.Combine(ExportsDirectory, "stale.mp4.tmp");
        string fresh = Path.Combine(ExportsDirectory, "fresh.mp4");
        foreach (string file in new[] { stale, staleTemp, fresh })
            await File.WriteAllTextAsync(file, "x");
        File.SetLastWriteTimeUtc(stale, clock.GetUtcNow().UtcDateTime.AddHours(-25));
        File.SetLastWriteTimeUtc(staleTemp, clock.GetUtcNow().UtcDateTime.AddHours(-25));

        await StartServiceAsync(clock);

        await WaitUntilAsync(() => !File.Exists(stale));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(fresh));

        clock.Advance(ExportService.MaxExportAge + ExportService.CleanupInterval);

        await WaitUntilAsync(() => !File.Exists(fresh));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(50);
        }

        Assert.True(condition(), "the condition was never met");
    }

    private async Task<ExportJob> WaitForAsync(string id, ExportState state)
    {
        for (int attempt = 0; attempt < 1_200; attempt++)
        {
            if (_service.Find(id) is { } job && (job.State == state || !job.IsActive))
            {
                Assert.Equal(state, job.State);
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Export {id} never reached {state}.");
    }

    /// <summary>Writes a 20 s fMP4 HLS VOD (with ENDLIST) as the session's index.m3u8.</summary>
    private async Task<RecordingSummary> SeedAsync(string id, string title)
    {
        string directory = Path.Combine(_root, "mock_nova", id);
        Directory.CreateDirectory(directory);
        await Cli.Wrap(_ffmpeg)
            .WithWorkingDirectory(directory)
            .WithArguments(
            [
                "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
                "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=15:duration=20",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=20",
                "-map", "0:v", "-map", "1:a",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "30",
                "-c:a", "aac", "-b:a", "64k",
                "-f", "hls", "-hls_segment_type", "fmp4", "-hls_time", "2",
                "-hls_playlist_type", "vod", "-hls_fmp4_init_filename", "init_001.mp4",
                "-hls_segment_filename", "p001_%05d.m4s",
                SessionStore.IndexFileName,
            ])
            .ExecuteAsync();

        RecordingSummary summary = Summary(id, title: title);
        _library.Upsert(summary);
        return summary;
    }

    /// <summary>
    /// Writes one HLS part per (size, encoder) as <c>init_NNN.mp4</c>/<c>pNNN_*.m4s</c> and joins
    /// them into the session index with discontinuities, the way the recorder lays out parts.
    /// </summary>
    private async Task<RecordingSummary> SeedPartsAsync(string id, params (string Size, string Encoder)[] parts)
    {
        string directory = Path.Combine(_root, "mock_nova", id);
        Directory.CreateDirectory(directory);
        var index = new List<string> { "#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-TARGETDURATION:2", "#EXT-X-PLAYLIST-TYPE:VOD" };
        for (int part = 1; part <= parts.Length; part++)
        {
            (string size, string encoder) = parts[part - 1];
            await Cli.Wrap(_ffmpeg)
                .WithWorkingDirectory(directory)
                .WithArguments(
                [
                    "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
                    "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=15:duration=4",
                    "-c:v", encoder, "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "30",
                    .. (encoder == "libx265" ? new[] { "-x265-params", "log-level=error" } : Array.Empty<string>()),
                    "-f", "hls", "-hls_segment_type", "fmp4", "-hls_time", "2",
                    "-hls_playlist_type", "vod", "-hls_fmp4_init_filename", PartFiles.Init(part),
                    "-hls_segment_filename", PartFiles.SegmentPattern(part),
                    PartFiles.Playlist(part),
                ])
                .ExecuteAsync();

            if (part > 1)
                index.Add("#EXT-X-DISCONTINUITY");
            index.AddRange((await File.ReadAllLinesAsync(Path.Combine(directory, PartFiles.Playlist(part))))
                .Where(line => line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal)
                    || line.StartsWith("#EXTINF", StringComparison.Ordinal)
                    || (line.Length > 0 && line[0] != '#')));
        }

        index.Add("#EXT-X-ENDLIST");
        await File.WriteAllLinesAsync(Path.Combine(directory, SessionStore.IndexFileName), index);

        RecordingSummary summary = Summary(id) with { DurationSeconds = 4 * parts.Length, PartCount = parts.Length };
        _library.Upsert(summary);
        return summary;
    }

    private static RecordingSummary Summary(string id, bool recording = false, string title = "Title") =>
        new(id, "mock_nova", title, "Game", StartedAt, recording ? null : StartedAt.AddMinutes(1),
            IsRecording: recording, DurationSeconds: recording ? null : 20, SizeBytes: 1, PartCount: 1,
            SegmentCount: 30, HasThumbnail: false, Chapters: []);
}
