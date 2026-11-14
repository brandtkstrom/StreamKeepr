using System.Globalization;
using CliWrap;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly SessionStoreTestContext _context = new();
    private readonly RecorderOptions _recorder;
    private readonly string _ffmpeg;
    private readonly ThumbnailService _service;

    public ThumbnailServiceTests()
    {
        _ffmpeg = TestPaths.RequireExecutable(
            "ffmpeg", "ffmpeg is required for thumbnail tests. Install it and put it on PATH.");
        _recorder = new RecorderOptions { FfmpegPath = _ffmpeg };
        _service = new ThumbnailService(
            _context.Library,
            _context.Store,
            Options.Create(new StorageOptions { RecordingsRoot = _context.Root }),
            Options.Create(_recorder),
            TimeProvider.System,
            NullLogger<ThumbnailService>.Instance);
    }

    private string TempDirectory => AppDataPaths.ThumbnailsDirectory(_context.Root);

    public void Dispose()
    {
        _service.Dispose();
        _context.Dispose();
    }

    [Theory]
    [InlineData(0d, false, null)]
    [InlineData(8d, false, 4d)]
    [InlineData(600d, false, 300d)]
    [InlineData(7200d, false, 300d)]
    [InlineData(8d, true, null)]
    [InlineData(300d, true, null)]
    [InlineData(301d, true, 300d)]
    public void Frame_offset_takes_five_minutes_in_or_the_midpoint_and_waits_while_live(
        double media, bool recording, double? expected)
    {
        Assert.Equal(expected, ThumbnailService.FrameOffset(media, recording));
    }

    [Fact]
    public void Frame_grab_refuses_names_that_would_split_the_concat_input()
    {
        Assert.Throws<ArgumentException>(
            () => FrameGrab.Arguments(new SegmentFiles("init_001.mp4", "a|b.m4s"), "out.jpg"));
    }

    [Fact]
    public async Task Sweep_grabs_a_jpeg_thumbnail_for_a_finished_recording_and_updates_the_library()
    {
        await SeedAsync(seconds: 8, segmentSeconds: 2, finish: true);

        await _service.SweepAsync(CancellationToken.None);

        byte[] image = await File.ReadAllBytesAsync(_context.ThumbnailPath);
        Assert.Equal([0xFF, 0xD8], image[..2]);
        Assert.True(_context.Library.Find(_context.Session.Id)!.HasThumbnail);
        Assert.Empty(Directory.EnumerateFiles(TempDirectory));
    }

    [Fact]
    public async Task Sweep_waits_until_a_live_recording_has_more_media_than_the_target_offset()
    {
        await SeedAsync(seconds: 8, segmentSeconds: 2, finish: false);

        await _service.SweepAsync(CancellationToken.None);

        Assert.False(File.Exists(_context.ThumbnailPath));
        Assert.False(_context.Library.Find(_context.Session.Id)!.HasThumbnail);
    }

    [Fact]
    public async Task Sweep_grabs_a_live_recording_once_it_passes_the_target_offset()
    {
        await SeedAsync(seconds: 320, segmentSeconds: 10, finish: false);

        await _service.SweepAsync(CancellationToken.None);

        Assert.True(File.Exists(_context.ThumbnailPath));
        RecordingSummary summary = _context.Library.Find(_context.Session.Id)!;
        Assert.True(summary.IsRecording);
        Assert.True(summary.HasThumbnail);
    }

    [Fact]
    public async Task Sweep_does_nothing_before_the_library_is_loaded()
    {
        await SeedAsync(seconds: 8, segmentSeconds: 2, finish: true, markLoaded: false);

        await _service.SweepAsync(CancellationToken.None);

        Assert.False(File.Exists(_context.ThumbnailPath));
    }

    [Fact]
    public async Task Sweep_retries_a_failed_grab_on_the_next_sweep()
    {
        await SeedAsync(seconds: 8, segmentSeconds: 2, finish: true);
        _recorder.FfmpegPath = FailingTool();

        await _service.SweepAsync(CancellationToken.None);
        Assert.False(File.Exists(_context.ThumbnailPath));
        Assert.Empty(Directory.EnumerateFiles(TempDirectory));

        _recorder.FfmpegPath = _ffmpeg;
        await _service.SweepAsync(CancellationToken.None);

        Assert.True(File.Exists(_context.ThumbnailPath));
    }

    [Fact]
    public async Task Sweep_gives_up_on_a_recording_after_max_attempts()
    {
        await SeedAsync(seconds: 8, segmentSeconds: 2, finish: true);
        _recorder.FfmpegPath = FailingTool();

        for (int attempt = 0; attempt < ThumbnailService.MaxAttempts; attempt++)
            await _service.SweepAsync(CancellationToken.None);

        _recorder.FfmpegPath = _ffmpeg;
        await _service.SweepAsync(CancellationToken.None);

        Assert.False(File.Exists(_context.ThumbnailPath));
        Assert.False(_context.Library.Find(_context.Session.Id)!.HasThumbnail);
    }

    [Fact]
    public async Task Starting_the_service_clears_leftover_temp_images_and_grabs_missing_thumbnails()
    {
        await SeedAsync(seconds: 8, segmentSeconds: 2, finish: true);
        Directory.CreateDirectory(TempDirectory);
        string leftover = Path.Combine(TempDirectory, "old.jpg.tmp");
        await File.WriteAllBytesAsync(leftover, [1]);

        await _service.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(_context.ThumbnailPath))
                await Task.Delay(50, deadline.Token);

            Assert.False(File.Exists(leftover));
        }
        finally
        {
            await _service.StopAsync(CancellationToken.None);
        }
    }

    private static string FailingTool() => TestPaths.RequireExecutable(
        "false", "the 'false' utility is required to simulate an FFmpeg failure.");

    /// <summary>
    /// Opens the context's session and records one real fMP4 part with the recorder's file
    /// names, then optionally finalizes it, so the library summary is the store's own.
    /// </summary>
    private async Task SeedAsync(double seconds, int segmentSeconds, bool finish, bool markLoaded = true)
    {
        await _context.Store.OpenAsync(_context.Session, _context.Live, CancellationToken.None);

        const int Rate = 2;
        string duration = seconds.ToString(CultureInfo.InvariantCulture);
        await Cli.Wrap(_ffmpeg)
            .WithWorkingDirectory(_context.Directory)
            .WithArguments(
            [
                "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
                "-f", "lavfi", "-i", $"testsrc2=size=64x36:rate={Rate}:duration={duration}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-g", (Rate * segmentSeconds).ToString(CultureInfo.InvariantCulture),
                "-f", "hls", "-hls_segment_type", "fmp4",
                "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),
                "-hls_list_size", "0", "-hls_playlist_type", "event",
                "-hls_flags", "+independent_segments",
                "-hls_fmp4_init_filename", PartFiles.Init(1),
                "-hls_segment_filename", PartFiles.SegmentPattern(1),
                PartFiles.Playlist(1),
            ])
            .ExecuteAsync();

        if (finish)
            await _context.Store.FinalizeAsync(_context.Session, CancellationToken.None);

        if (markLoaded)
            _context.Library.MarkLoaded();
    }
}
