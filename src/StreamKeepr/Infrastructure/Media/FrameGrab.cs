using System.Text;
using CliWrap;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Decodes the first video frame of one fMP4 media segment into a JPEG. FFmpeg reads the init
/// segment and the media segment back to back through its <c>concat:</c> protocol, so no HLS
/// demuxing or seeking is involved and live and finished sessions work the same way. Recorded
/// segments start on a keyframe (independent segments), so the first frame is a clean picture.
/// </summary>
public static class FrameGrab
{
    /// <summary>The widest thumbnail written; smaller sources are never upscaled.</summary>
    public const int MaxWidth = 640;

    /// <summary>
    /// The FFmpeg command line. <paramref name="files"/> are plain names resolved against the
    /// session folder, which is FFmpeg's working directory.
    /// </summary>
    public static IReadOnlyList<string> Arguments(SegmentFiles files, string output)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        // '|' separates concat inputs; recorder file names never contain it.
        if (files.Init.Contains('|') || files.Media.Contains('|'))
            throw new ArgumentException("Segment file names cannot contain '|'.", nameof(files));

        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", $"concat:{files.Init}|{files.Media}",
            "-map", "0:v:0", "-frames:v", "1",
            "-vf", $"scale='min({MaxWidth},iw)':-2",
            "-q:v", "4",
            "-f", "mjpeg",
            output,
        ];
    }

    /// <summary>Writes the frame to <paramref name="output"/>; throws when FFmpeg fails.</summary>
    public static async Task RunAsync(
        string ffmpegPath, string sessionDir, SegmentFiles files, string output, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);

        var stderr = new StringBuilder();
        CommandResult result = await Cli.Wrap(ffmpegPath)
            .WithWorkingDirectory(sessionDir)
            .WithArguments(Arguments(files, output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException(
                $"ffmpeg exited {result.ExitCode} grabbing a frame from {files.Media}: {stderr.ToString().Trim()}");
        }
    }
}
