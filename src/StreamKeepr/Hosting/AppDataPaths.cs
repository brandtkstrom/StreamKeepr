namespace StreamKeepr.Hosting;

/// <summary>
/// Private application data stored on the recordings volume. Nothing under this
/// folder is media, and the media allowlist must never expose it.
/// </summary>
public static class AppDataPaths
{
    public const string AppDataDirectoryName = ".streamkeepr";
    public const string KeysDirectoryName = "keys";

    public static string AppDataDirectory(string recordingsRoot) =>
        Path.Combine(RequireRoot(recordingsRoot), AppDataDirectoryName);

    public static string KeysDirectory(string recordingsRoot) =>
        Path.Combine(AppDataDirectory(recordingsRoot), KeysDirectoryName);

    private static string RequireRoot(string recordingsRoot) =>
        string.IsNullOrWhiteSpace(recordingsRoot)
            ? throw new ArgumentException("A recordings root is required.", nameof(recordingsRoot))
            : recordingsRoot;
}
