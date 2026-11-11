using StreamKeepr.Configuration;

namespace StreamKeepr.Mock;

/// <summary>
/// Wires the <c>Mock</c> environment: a disposable recordings root under the system temp
/// directory, dummy Twitch credentials, and the existing fake Streamlink fixture. The Mock
/// profile deliberately ignores <c>Storage:RecordingsRoot</c> and any user-secret values, so a
/// developer launch can never point it at production media or credentials.
/// </summary>
public static class MockEnvironment
{
    public const string Name = "Mock";
    public const string DataDirectoryName = "streamkeepr-mock";
    public const string RecordingsDirectoryName = "recordings";
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

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:RecordingsRoot"] = recordingsRoot,
            ["Storage:MinFreeGb"] = "1",
            ["Twitch:ClientId"] = ClientId,
            ["Twitch:ClientSecret"] = ClientSecret,
            ["Twitch:PollIntervalSeconds"] = "10",
            ["Recorder:StreamlinkPath"] = streamlink,
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
            options.StreamlinkPath = streamlink;
            options.TwitchUserOAuthToken = null;
        });
    }
}
