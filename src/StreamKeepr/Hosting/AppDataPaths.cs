namespace StreamKeepr.Hosting;

/// <summary>
/// Private application data. The <c>.streamkeepr</c> folder is on the recordings volume; nothing
/// under it is media, and the media allowlist must never expose it. The Data Protection key ring
/// lives beside the record store instead (<see cref="DataProtectionKeysDirectory"/>).
/// </summary>
public static class AppDataPaths
{
    public const string AppDataDirectoryName = ".streamkeepr";
    public const string KeysDirectoryName = "keys";
    public const string LogsDirectoryName = "logs";

    /// <summary>Legacy admin-token file; the record store replaced it (ADR-0014). Never read or written.</summary>
    public const string AdminTokensFileName = "admin-tokens.json";

    /// <summary>Legacy conduit-state file; the record store replaced it (ADR-0014). Never read or written.</summary>
    public const string EventSubConduitFileName = "eventsub-conduit.json";

    public const string ChannelsFileName = "channels.json";
    /// <summary>Legacy resume-position file; the record store replaced it (ADR-0014). Never read or written.</summary>
    public const string ProgressFileName = "progress.json";
    public const string ExportsDirectoryName = "exports";
    public const string ThumbnailsDirectoryName = "thumbnails";

    public static string AppDataDirectory(string recordingsRoot) =>
        Path.Combine(RequireRoot(recordingsRoot), AppDataDirectoryName);

    /// <summary>
    /// The Data Protection key ring: a <c>keys</c> folder in the directory of
    /// <c>Storage:DatabasePath</c>, so the keys sit on the same volume as the ciphertext they
    /// protect (the admin token row) and never on the media volume.
    /// </summary>
    public static string DataProtectionKeysDirectory(string databasePath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(
                string.IsNullOrWhiteSpace(databasePath)
                    ? throw new ArgumentException("A database path is required.", nameof(databasePath))
                    : databasePath))!,
            KeysDirectoryName);

    /// <summary>Compact-JSON app logs; private app data, never media.</summary>
    public static string LogsDirectory(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), LogsDirectoryName);

    public static string ChannelsFile(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), ChannelsFileName);

    public static string ExportsDirectory(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), ExportsDirectoryName);

    /// <summary>Scratch space for thumbnail grabs before the session store moves them into place.</summary>
    public static string ThumbnailsDirectory(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), ThumbnailsDirectoryName);

    private static string RequireRoot(string recordingsRoot) =>
        string.IsNullOrWhiteSpace(recordingsRoot)
            ? throw new ArgumentException("A recordings root is required.", nameof(recordingsRoot))
            : recordingsRoot;
}
