using System.Globalization;
using System.Text;
using CliWrap;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>
/// Generates the short H.264/AAC fMP4 HLS clip used by every seeded mock recording. One clip
/// is generated at startup and copied to each session, so seeding stays fast and deterministic.
/// </summary>
internal static class MockMedia
{
    public const string TemplatePlaylist = "p001.m3u8";
    public const string TemplateInit = "init_001.mp4";
    public const string Thumbnail = "thumb.jpg";

    // Longer than the 30 s resume end margin (ProgressStore.EndMargin) so a mock recording can
    // keep a watch position; at 320x180 and 15 fps the clip stays small and quick to generate.
    private const int ClipSeconds = 60;

    /// <summary>Length of one generated part; every seeded part has this many seconds.</summary>
    public static readonly TimeSpan PartDuration = TimeSpan.FromSeconds(ClipSeconds);

    public static async Task GenerateTemplateAsync(
        string ffmpegPath, string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        string[] mediaArgs =
        [
            "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
            "-f", "lavfi", "-i",
            $"testsrc2=size=320x180:rate=15:duration={ClipSeconds}",
            "-f", "lavfi", "-i",
            $"sine=frequency=440:sample_rate=48000:duration={ClipSeconds}",
            "-map", "0:v", "-map", "1:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-pix_fmt", "yuv420p", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "64k",
            "-f", "hls",
            "-hls_segment_type", "fmp4",
            "-hls_time", "2",
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "+independent_segments+program_date_time",
            "-hls_fmp4_init_filename", TemplateInit,
            "-hls_segment_filename", "p001_%05d.m4s",
            TemplatePlaylist,
        ];

        await RunAsync(ffmpegPath, directory, mediaArgs, "mock HLS clip", ct);

        string[] thumbnailArgs =
        [
            "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=1:duration=1",
            "-frames:v", "1", "-q:v", "4", Thumbnail,
        ];

        await RunAsync(ffmpegPath, directory, thumbnailArgs, "mock thumbnail", ct);
    }

    /// <summary>
    /// Renames the template part to <paramref name="part"/> and replaces FFmpeg's wall-clock
    /// program-date-time stamps with deterministic session-relative times.
    /// </summary>
    public static string RenderPartPlaylist(
        string template, int part, DateTimeOffset partStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentOutOfRangeException.ThrowIfLessThan(part, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(part, 999);

        string renamed = template
            .Replace("p001", $"p{part:D3}", StringComparison.Ordinal)
            .Replace("init_001", $"init_{part:D3}", StringComparison.Ordinal);

        var playlist = new StringBuilder(renamed.Length);
        double elapsed = 0;
        double pendingDuration = 0;

        foreach (string raw in renamed.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal))
            {
                playlist.Append("#EXT-X-PROGRAM-DATE-TIME:")
                    .Append(FormatTime(partStart.AddSeconds(elapsed)))
                    .Append('\n');
                continue;
            }

            playlist.Append(line).Append('\n');

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line.AsSpan("#EXTINF:".Length);
                int comma = value.IndexOf(',');
                if (comma >= 0)
                    value = value[..comma];

                if (double.TryParse(
                        value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    pendingDuration = parsed;
                }
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                elapsed += pendingDuration;
                pendingDuration = 0;
            }
        }

        return playlist.ToString();
    }

    /// <summary>Copies the template media to a seeded part number.</summary>
    public static void CopyPartFiles(string templateDirectory, string sessionDirectory, int part)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);

        foreach (string source in Directory.EnumerateFiles(templateDirectory, "p001_*"))
        {
            string name = Path.GetFileName(source)
                .Replace("p001", $"p{part:D3}", StringComparison.Ordinal);
            File.Copy(source, Path.Combine(sessionDirectory, name), overwrite: true);
        }

        File.Copy(
            Path.Combine(templateDirectory, TemplateInit),
            Path.Combine(sessionDirectory, $"init_{part:D3}.mp4"),
            overwrite: true);
    }

    private static async Task RunAsync(
        string ffmpegPath,
        string directory,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken ct)
    {
        CommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithWorkingDirectory(directory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Mock media generation failed ({description}): ffmpeg exited " +
                $"with code {result.ExitCode}.");
        }
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
