using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>The video parameters that must match for parts to be stream-copied into one MP4.</summary>
public sealed record PartVideo(string Codec, string? Profile, int Width, int Height, string? PixelFormat)
{
    public bool IsHevc => Codec == "hevc";
}

/// <summary>
/// Reads each part's init segment with ffprobe before an export is queued. A recording whose
/// parts differ in codec, profile, size, or pixel format cannot become one stream-copied MP4.
/// </summary>
public static class ExportPartProbe
{
    private const string MapPrefix = "#EXT-X-MAP:URI=\"";

    /// <summary>
    /// The init segments the session index references, in order and without duplicates. Only
    /// plain file names next to the index are accepted, so a playlist never points the probe
    /// elsewhere.
    /// </summary>
    public static IReadOnlyList<string> InitFiles(string indexPath)
    {
        string directory = Path.GetDirectoryName(indexPath)!;
        var files = new List<string>();
        foreach (string line in File.ReadLines(indexPath))
        {
            if (!line.StartsWith(MapPrefix, StringComparison.Ordinal) || !line.EndsWith('"'))
                continue;

            string name = line[MapPrefix.Length..^1];
            if (name.Length == 0 || Path.GetFileName(name) != name)
                continue;

            string path = Path.Combine(directory, name);
            if (!files.Contains(path, StringComparer.Ordinal))
                files.Add(path);
        }

        return files;
    }

    public static async Task<PartVideo> ProbeAsync(string ffprobePath, string file, CancellationToken ct)
    {
        BufferedCommandResult result = await Cli.Wrap(ffprobePath)
            .WithArguments(
            [
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=codec_name,profile,width,height,pix_fmt",
                "-of", "json", file,
            ])
            .ExecuteBufferedAsync(Encoding.UTF8, ct);

        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);
        JsonElement stream = json.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
        if (stream.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"No video stream in {Path.GetFileName(file)}.");

        return new PartVideo(
            stream.GetProperty("codec_name").GetString() ?? "",
            OptionalString(stream, "profile"),
            stream.TryGetProperty("width", out JsonElement width) ? width.GetInt32() : 0,
            stream.TryGetProperty("height", out JsonElement height) ? height.GetInt32() : 0,
            OptionalString(stream, "pix_fmt"));
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
}
