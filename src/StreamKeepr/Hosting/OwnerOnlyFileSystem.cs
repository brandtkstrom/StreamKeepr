namespace StreamKeepr.Hosting;

/// <summary>
/// Creates private app-data folders and tightens their permissions on Unix.
/// Windows has no equivalent mode bits, so all calls are no-ops there.
/// </summary>
internal static class OwnerOnlyFileSystem
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates the key directory (and its parent app-data directory), proves it is
    /// writable, and gives the owner exclusive access. Throws an actionable error
    /// when the recordings volume is not writable by the app user.
    /// </summary>
    public static void EnsureWritableKeyDirectory(string keyDirectory)
    {
        try
        {
            Directory.CreateDirectory(keyDirectory);
            SetOwnerOnlyDirectory(Path.GetDirectoryName(keyDirectory)!);
            SetOwnerOnlyDirectory(keyDirectory);
            EnforceOwnerOnlyKeyFiles(keyDirectory);

            var probePath = Path.Combine(keyDirectory, $".write-probe-{Guid.NewGuid():N}");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerOnlyFileMode;

            try
            {
                using var probe = new FileStream(probePath, options);
                probe.WriteByte(0);
            }
            finally
            {
                File.Delete(probePath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The Data Protection key directory '{keyDirectory}' is not writable. " +
                "Ensure the recordings volume is owned by the app user (UID 1654 in the container; " +
                "chown -R 1654:1654 <recordings>) or use a Docker named volume.",
                ex);
        }
    }

    /// <summary>
    /// Creates the private app-data folder and gives the owner exclusive access.
    /// </summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        SetOwnerOnlyDirectory(directory);
    }

    /// <summary>
    /// Writes a private file through a sibling temp file and renames it into
    /// place, so readers never observe a partial payload.
    /// </summary>
    public static void WriteAtomically(string filePath, ReadOnlySpan<byte> contents)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("A file path with a directory is required.", nameof(filePath));
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerOnlyFileMode;

            using (var stream = new FileStream(tempPath, options))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            // Publish only after the mode is 0600 so the token is never briefly readable.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempPath, OwnerOnlyFileMode);

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Applies owner-only permissions to every persisted key and revocation file.
    /// </summary>
    public static void EnforceOwnerOnlyKeyFiles(string keyDirectory)
    {
        if (OperatingSystem.IsWindows())
            return;

        foreach (var keyFile in Directory.EnumerateFiles(keyDirectory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.SetUnixFileMode(keyFile, OwnerOnlyFileMode);
            }
            catch (FileNotFoundException)
            {
                // Another actor removed the key between enumeration and the mode change.
            }
        }
    }

    private static void SetOwnerOnlyDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, DirectoryMode);
    }
}
