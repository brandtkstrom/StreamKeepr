using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Gives every recording a <c>thumb.jpg</c> grabbed from its own media (decision E-01). Once the
/// library is loaded and then every <see cref="SweepInterval"/>, it walks the library and, one
/// FFmpeg at a time, grabs the frame at <see cref="FrameOffset"/> for each recording without a
/// thumbnail. The image is written to <c>.streamkeepr/thumbnails</c> first and handed to
/// <see cref="SessionStore.SaveThumbnailAsync"/>, which owns the session folder. A recording that
/// fails <see cref="MaxAttempts"/> times is left without a thumbnail until the next restart.
/// </summary>
public sealed class ThumbnailService(
    RecordingLibrary library,
    SessionStore store,
    IOptions<StorageOptions> storage,
    IOptions<RecorderOptions> recorder,
    TimeProvider clock,
    ILogger<ThumbnailService> logger) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far into a recording the frame is taken, past most intro and "starting soon" screens.
    /// A live recording waits until it has more media than this.
    /// </summary>
    public static readonly TimeSpan TargetOffset = TimeSpan.FromMinutes(5);

    public const int MaxAttempts = 3;

    // A hung FFmpeg must not stall every later grab.
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(30);

    // Only the sweep touches this, and sweeps never overlap.
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);

    private string TempDirectory => AppDataPaths.ThumbnailsDirectory(storage.Value.RecordingsRoot);

    /// <summary>
    /// Where to take the frame, in seconds on the session timeline, or null when it is too early.
    /// Finished recordings use <see cref="TargetOffset"/> or their midpoint, whichever is earlier.
    /// </summary>
    public static double? FrameOffset(double mediaSeconds, bool recording)
    {
        double target = TargetOffset.TotalSeconds;
        if (recording)
            return mediaSeconds > target ? target : null;

        return mediaSeconds > 0 ? Math.Min(target, mediaSeconds / 2) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Created before the first await so the first periodic tick is never missed.
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            await Task.Yield();
            SweepTempDirectory();
            do
            {
                await SweepAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown; an interrupted grab left only a temp file, swept at the next start.
        }
    }

    /// <summary>
    /// One pass over the library. Does nothing until startup recovery has loaded it. Callers
    /// must not run two sweeps at once; the background loop never does.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        if (!library.IsLoaded)
            return;

        foreach (RecordingSummary summary in library.All)
        {
            ct.ThrowIfCancellationRequested();
            if (summary.HasThumbnail || _failures.GetValueOrDefault(summary.Id) >= MaxAttempts)
                continue;

            await TryGrabAsync(summary, ct);
        }
    }

    private async Task TryGrabAsync(RecordingSummary summary, CancellationToken stoppingToken)
    {
        if (!RecordingsPath.TryResolve(
                storage.Value.RecordingsRoot, [summary.Channel, summary.Id], out string directory))
        {
            return;
        }

        string temp = Path.Combine(TempDirectory, $"{summary.Id}.jpg.tmp");
        try
        {
            // A deleted or unreadable session has no playlist; the library drops it separately.
            if (await store.StitchLiveAsync(directory, stoppingToken) is not { } playlist)
                return;

            if (FrameOffset(PlaylistStitcher.TotalDuration(playlist), summary.IsRecording) is not { } offset)
                return;

            SegmentFiles files = PlaylistStitcher.SegmentAt(playlist, offset)
                ?? throw new InvalidDataException("No usable segment covers the thumbnail position.");

            Directory.CreateDirectory(TempDirectory);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                timeout.CancelAfter(GrabTimeout);
                await FrameGrab.RunAsync(recorder.Value.FfmpegPath, directory, files, temp, timeout.Token);
            }

            if (await store.SaveThumbnailAsync(directory, temp, stoppingToken))
                logger.LogInformation("Saved a thumbnail for {RecordingId} from {Segment}.", summary.Id, files.Media);

            _failures.Remove(summary.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            // Thumbnails are best effort: a failure never affects the recording or the next grab.
            TryDelete(temp);
            int failures = _failures[summary.Id] = _failures.GetValueOrDefault(summary.Id) + 1;
            logger.LogWarning(
                ex,
                "Could not grab a thumbnail for {RecordingId} (attempt {Attempt} of {MaxAttempts}).",
                summary.Id, failures, MaxAttempts);
        }
    }

    /// <summary>Removes temp images left by a grab that a shutdown or crash interrupted.</summary>
    private void SweepTempDirectory()
    {
        try
        {
            if (!Directory.Exists(TempDirectory))
                return;

            foreach (string file in Directory.EnumerateFiles(TempDirectory).ToArray())
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clear leftover thumbnail temp files.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the thumbnail temp file {Path}.", path);
        }
    }
}
