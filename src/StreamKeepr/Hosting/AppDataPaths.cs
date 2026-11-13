namespace StreamKeepr.Hosting;

/// <summary>
/// Private application data stored on the recordings volume. Nothing under this
/// folder is media, and the media allowlist must never expose it.
/// </summary>
public static class AppDataPaths
{
    public const string AppDataDirectoryName = ".streamkeepr";
    public const string KeysDirectoryName = "keys";
    public const string AdminTokensFileName = "admin-tokens.json";
    public const string EventSubConduitFileName = "eventsub-conduit.json";
    public const string ChannelsFileName = "channels.json";
    public const string ProgressFileName = "progress.json";
    public const string ExportsDirectoryName = "exports";
    public const string ThumbnailsDirectoryName = "thumbnails";

    public static string AppDataDirectory(string recordingsRoot) =>
        Path.Combine(RequireRoot(recordingsRoot), AppDataDirectoryName);

    public static string KeysDirectory(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), KeysDirectoryName);

    public static string AdminTokensFile(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), AdminTokensFileName);

    public static string EventSubConduitFile(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), EventSubConduitFileName);

    public static string ChannelsFile(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), ChannelsFileName);

    public static string ProgressFile(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), ProgressFileName);

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
