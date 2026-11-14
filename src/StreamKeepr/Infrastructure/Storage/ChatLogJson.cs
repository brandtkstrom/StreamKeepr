using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads and writes the chat log's JSON and JSONL files. Live reads are tolerant, because a
/// writer may be mid-append; rewrite reads are strict, so a purge never drops a row it could
/// not parse. Whole-file writes go through a sibling temp file so readers never see a partial
/// file.
/// </summary>
internal static class ChatLogJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Reads a live log tolerantly: a failure is logged and returns what was read.</summary>
    public static List<T> ReadLinesTolerant<T>(string path, ILogger logger)
    {
        var items = new List<T>();
        if (!File.Exists(path))
            return items;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            items.AddRange(ParseLines<T>(ReadLines(reader), path, logger));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Chat log {Path} could not be fully read; using {Count} entries.",
                path, items.Count);
        }

        return items;
    }

    /// <summary>
    /// Parses a finished log for a rewrite. A malformed line throws instead of being skipped,
    /// so the purge never deletes a row it could not read.
    /// </summary>
    public static List<T> ReadLinesStrict<T>(string path)
    {
        var items = new List<T>();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                if (JsonSerializer.Deserialize<T>(line, Options) is { } item)
                    items.Add(item);
            }
            catch (JsonException ex)
            {
                throw new JsonException($"Line {lineNumber} of {path} is not valid chat JSON.", ex);
            }
        }

        return items;
    }

    public static T? TryRead<T>(string path, ILogger logger)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                : default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Chat metadata {Path} could not be read.", path);
            return default;
        }
    }

    /// <summary>Writes through a sibling temp file, the storage convention, so readers never see a partial file.</summary>
    public static void WriteAllTextAtomically(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public static string ToJsonLines<T>(IEnumerable<T> items) =>
        string.Join('\n', items.Select(Serialize)) + "\n";

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>
    /// Parses JSON lines, skipping malformed ones. A torn last line is expected while a writer
    /// is mid-append, so a skip is logged at debug level only.
    /// </summary>
    private static IEnumerable<T> ParseLines<T>(IEnumerable<string> lines, string path, ILogger logger)
    {
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            T? item;
            try
            {
                item = JsonSerializer.Deserialize<T>(line, Options);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Skipping a malformed chat log line in {Path}.", path);
                continue;
            }

            if (item is not null)
                yield return item;
        }
    }
}
