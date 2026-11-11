using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;
using CliWrap;

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
    private ExportService _service = null!;
    private string _ffmpeg = null!;

    public async Task InitializeAsync()
    {
        _ffmpeg = TestPaths.RequireExecutable(
            "ffmpeg", "ffmpeg is required for export tests. Install it and put it on PATH.");
        _service = new ExportService(
            _library,
            _leases,
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            Options.Create(new RecorderOptions { FfmpegPath = _ffmpeg }),
            TimeProvider.System,
            NullLogger<ExportService>.Instance);
        await _service.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
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

    private static RecordingSummary Summary(string id, bool recording = false, string title = "Title") =>
        new(id, "mock_nova", title, "Game", StartedAt, recording ? null : StartedAt.AddMinutes(1),
            IsRecording: recording, DurationSeconds: recording ? null : 20, SizeBytes: 1, PartCount: 1,
            SegmentCount: 30, HasThumbnail: false, Chapters: []);
}
