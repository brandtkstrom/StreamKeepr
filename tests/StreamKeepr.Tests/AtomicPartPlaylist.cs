namespace StreamKeepr.Tests;

/// <summary>
/// Writes a part playlist the way FFmpeg does: to <c>pNNN.m3u8.tmp</c> in the same directory,
/// then renamed over <c>pNNN.m3u8</c>. A live reader therefore sees the old complete file or
/// the new complete file, never a partial one (session-store contract, "Atomic writes").
/// </summary>
internal static class AtomicPartPlaylist
{
    private const int MaxMoveAttempts = 100;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(2);

    public static async Task WriteAsync(string path, string content)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content);
        await MoveWithRetryAsync(temp, path);
    }

    /// <summary>
    /// Models FFmpeg's temp-file rename. Windows may reject the replace while a reader still
    /// holds the final path, so the writer retries; the reader never caches a handle.
    /// </summary>
    private static async Task MoveWithRetryAsync(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MaxMoveAttempts)
                    throw;
                await Task.Delay(MoveRetryDelay);
            }
        }
    }
}
