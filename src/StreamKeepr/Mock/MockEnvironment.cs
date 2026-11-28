using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Mock;

/// <summary>
/// Wires the <c>Mock</c> environment: a disposable recordings root and a per-process record store
/// under the system temp directory, dummy Twitch credentials, and the existing fake Streamlink
/// fixture. The Mock profile deliberately ignores <c>Storage:RecordingsRoot</c>,
/// <c>Storage:DatabasePath</c> and any user-secret values, so a developer launch can never point
/// it at production media, records, or credentials.
/// </summary>
public static class MockEnvironment
{
    public const string Name = "Mock";
    public const string DataDirectoryName = "streamkeepr-mock";
    public const string RecordingsDirectoryName = "recordings";
    public const string RecordStoreDirectoryPrefix = "records-";
    public const string StreamlinkFixtureRelativePath = "tests/fixtures/fake-live-streamlink";

    public const string ClientId = "mock-client-id";
    public const string ClientSecret = "mock-client-secret";

    /// <summary>Resolves the disposable root, refusing any path outside the system temp directory.</summary>
    public static string ResolveRecordingsRoot(string? configuredRoot, string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);

        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(tempPath, DataDirectoryName, RecordingsDirectoryName)
            : configuredRoot;
        string fullRoot = Canonicalize(root);
        string fullTemp = Canonicalize(tempPath);
        string relative = Path.GetRelativePath(fullTemp, fullRoot);

        if (Path.IsPathRooted(relative)
            || relative is "." or ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Mock recordings root must be a directory below the system temp directory " +
                $"('{fullTemp}'); got '{fullRoot}'. Set Mock:RecordingsRoot to a temp path.");
        }

        return Path.GetFullPath(root);
    }

    /// <summary>
    /// A record store for this process only, beside (never inside) the recordings root, which
    /// the seeder may reset. ADR-0014: it is the only database the app ever deletes.
    /// </summary>
    public static string NewRecordStorePath(string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        return Path.GetFullPath(Path.Combine(
            tempPath,
            DataDirectoryName,
            $"{RecordStoreDirectoryPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}",
            StorageOptions.DatabaseFileName));
    }

    /// <summary>
    /// Deletes this process's record store, with its WAL files and backups, after a clean
    /// shutdown. Every store has stopped by then, and with pooling off nothing holds the file.
    /// </summary>
    public static void DeleteRecordStoreOnCleanShutdown(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        string databasePath = app.Services
            .GetRequiredService<IOptions<StorageOptions>>().Value.DatabasePath;
        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DataDirectoryName));

        // Only ever the directory NewRecordStorePath made: never a configured or shared path.
        if (!string.Equals(Path.GetDirectoryName(directory), expectedParent, StringComparison.Ordinal)
            || !Path.GetFileName(directory).StartsWith(RecordStoreDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Mock record store '{databasePath}' is not a per-process temp database.");
        }

        app.Lifetime.ApplicationStopped.Register(() =>
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogWarning(ex, "Could not delete the Mock record store at {Directory}.", directory);
            }
        });
    }

    /// <summary>
    /// Resolves every existing symlink component (for example macOS <c>/var</c> →
    /// <c>/private/var</c>) so the temp-root check cannot be bypassed or falsely rejected.
    /// Missing trailing components are kept as-is.
    /// </summary>
    private static string Canonicalize(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException($"'{path}' has no filesystem root.");

        foreach (string part in full[current.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            var info = new DirectoryInfo(next);
            current = info.Exists && info.LinkTarget is not null
                ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next
                : next;
        }

        return Path.GetFullPath(current);
    }

    /// <summary>
    /// Applies Mock configuration before options are bound. In-memory configuration is added last
    /// so environment variables cannot supply real Twitch credentials or a production media root.
    /// </summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string recordingsRoot = ResolveRecordingsRoot(
            builder.Configuration["Mock:RecordingsRoot"], Path.GetTempPath());
        string streamlink = Path.GetFullPath(Path.Combine(
            builder.Environment.ContentRootPath, "..", "..", StreamlinkFixtureRelativePath));

        if (!File.Exists(streamlink))
        {
            throw new InvalidOperationException(
                $"The Mock profile needs the fake Streamlink fixture at '{streamlink}'. " +
                "Run it from the repository checkout (dotnet run --project src/StreamKeepr).");
        }

        string startableStreamlink = PlatformScript.ResolveForHost(streamlink);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "false",
            ["Storage:RecordingsRoot"] = recordingsRoot,
            ["Storage:DatabasePath"] = NewRecordStorePath(Path.GetTempPath()),
            ["Storage:MinFreeGb"] = "1",
            ["Twitch:ClientId"] = ClientId,
            ["Twitch:ClientSecret"] = ClientSecret,
            ["Twitch:PollIntervalSeconds"] = "10",
            ["Recorder:StreamlinkPath"] = startableStreamlink,
            ["Recorder:SegmentSeconds"] = "2",
            ["Recorder:HealthyPartSeconds"] = "5",
        });

        // Configuration providers cannot remove keys supplied by environment variables, so
        // normalize the bound options as well: Mock never carries a real channel list, secret,
        // or subscriber token.
        builder.Services.PostConfigure<TwitchOptions>(options =>
        {
            options.ClientId = ClientId;
            options.ClientSecret = ClientSecret;
            options.Channels.Clear();
        });

        builder.Services.PostConfigure<RecorderOptions>(options =>
        {
            options.StreamlinkPath = startableStreamlink;
            options.TwitchUserOAuthToken = null;
        });
    }
}
