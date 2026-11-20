namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The one read path for session files that the recorder may replace or delete concurrently:
/// part playlists, the finalized index, and the manifest. Opening with read/write/delete sharing
/// and asynchronous options keeps a reader from blocking the recorder's atomic replace on
/// Windows; on POSIX filesystems sharing is advisory and therefore best effort.
/// </summary>
internal static class PartPlaylistReader
{
    public static FileStream OpenSharedRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = OpenSharedRead(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads a file, or returns null when it disappeared before it could be opened.</summary>
    public static async Task<string?> TryReadAllTextAsync(string path, CancellationToken ct)
    {
        try
        {
            return await ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
