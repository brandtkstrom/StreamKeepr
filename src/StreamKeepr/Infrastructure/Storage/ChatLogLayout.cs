using System.Globalization;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Where a session's chat log lives and how its files are named: <c>chat/meta.json</c>,
/// <c>chat/events.jsonl</c>, and one UTC time bucket of messages per <c>yyyyMMddTHHmmZ.jsonl</c>
/// (ADR 0009). Listing is tolerant: an unreadable folder is logged and reads as no chat.
/// </summary>
public static class ChatLogLayout
{
    public const string ChatFolderName = "chat";
    public const string MetaFileName = "meta.json";
    public const string EventsFileName = "events.jsonl";

    private const string JsonLinesPattern = "*.jsonl";

    public static string ChatDirectory(string sessionDirectory) =>
        Path.Combine(sessionDirectory, ChatFolderName);

    public static string MetaPath(string sessionDirectory) =>
        Path.Combine(ChatDirectory(sessionDirectory), MetaFileName);

    public static string EventsPath(string sessionDirectory) =>
        Path.Combine(ChatDirectory(sessionDirectory), EventsFileName);

    /// <summary>The UTC start of the bucket that holds messages sent at <paramref name="at"/>.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset at, int bucketSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketSeconds, 1);
        long seconds = at.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(
            seconds - (((seconds % bucketSeconds) + bucketSeconds) % bucketSeconds));
    }

    /// <summary>The bucket file holding messages sent at <paramref name="at"/>.</summary>
    public static string BucketFileName(DateTimeOffset at, int bucketSeconds) =>
        BucketStart(at, bucketSeconds).UtcDateTime.ToString(
            "yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture) + ".jsonl";

    public static bool HasChat(string sessionDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string directory = ChatDirectory(sessionDirectory);
        try
        {
            if (File.Exists(Path.Combine(directory, MetaFileName)))
                return true;

            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, JsonLinesPattern).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not check {Directory} for chat; treating it as having none.",
                directory);
            return false;
        }
    }

    /// <summary>Message bucket files in time order; empty when there is no readable chat log.</summary>
    public static IReadOnlyList<string> BucketFiles(string sessionDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string directory = ChatDirectory(sessionDirectory);
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.EnumerateFiles(directory, JsonLinesPattern)
                .Where(path => !string.Equals(
                    Path.GetFileName(path), EventsFileName, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list chat buckets in {Directory}.", directory);
            return [];
        }
    }
}
