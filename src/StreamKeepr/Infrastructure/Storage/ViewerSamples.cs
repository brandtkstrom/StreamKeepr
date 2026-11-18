using System.Text;
using System.Text.Json;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>One viewer-count sample: <see cref="At"/> seconds from the session start.</summary>
public sealed record ViewerSample(double At, int Count);

/// <summary>
/// The append-only <c>viewers.jsonl</c> in a session folder (session-store contract §7.3): one
/// <c>{"v":1,"at":…,"count":…}</c> object per line. This type owns the line format; the
/// coordinator decides when to sample and <see cref="SessionStore"/> serializes the appends.
/// </summary>
public static class ViewerSamples
{
    public const string FileName = "viewers.jsonl";
    public const int Version = 1;

    /// <summary>The most samples one read returns; later lines are ignored.</summary>
    public const int MaxSamples = 50_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Seconds from <paramref name="startedAt"/> to <paramref name="at"/>, to 0.1 s and never negative.</summary>
    public static double OffsetSeconds(DateTimeOffset startedAt, DateTimeOffset at) =>
        Math.Max(0, RoundSeconds((at - startedAt).TotalSeconds));

    /// <summary>Rounds to the 0.1 s precision of the viewer timeline.</summary>
    public static double RoundSeconds(double seconds) =>
        Math.Round(seconds, 1, MidpointRounding.AwayFromZero);

    /// <summary>One serialized line, without its terminating newline.</summary>
    public static string FormatLine(double at, int count) =>
        JsonSerializer.Serialize(new Line(Version, at, count), Json);

    /// <summary>
    /// Reads the samples in <paramref name="sessionDirectory"/>, oldest first. A missing file is no
    /// samples. A malformed or partial line is skipped with one warning per read, never thrown.
    /// </summary>
    public static IReadOnlyList<ViewerSample> Read(string sessionDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        string path = Path.Combine(sessionDirectory, FileName);
        var samples = new List<ViewerSample>();
        int skipped = 0;

        try
        {
            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                Encoding.UTF8);

            while (samples.Count < MaxSamples && reader.ReadLine() is { } text)
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (TryParse(text, out ViewerSample? sample))
                    samples.Add(sample);
                else
                    skipped++;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }

        if (skipped > 0)
        {
            logger.LogWarning(
                "Skipped {Count} malformed viewer sample line(s) in {Path}.", skipped, path);
        }

        return samples;
    }

    private static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ViewerSample? sample)
    {
        sample = null;
        Line? line;
        try
        {
            line = JsonSerializer.Deserialize<Line>(text, Json);
        }
        catch (JsonException)
        {
            return false;
        }

        // Missing fields deserialize as null, so a truncated object is rejected, not zeroed.
        if (line is not { V: Version, At: double at, Count: int count } || !double.IsFinite(at) || at < 0 || count < 0)
            return false;

        sample = new ViewerSample(at, count);
        return true;
    }

    private sealed record Line(int? V, double? At, int? Count);
}
