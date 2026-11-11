using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Tests.Hosting;

public sealed class ToolPreflightTests
{
    [Fact]
    public void Missing_ffmpeg_fails_startup_and_names_the_tool()
    {
        using var temp = new TempDirectory();
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [Setting(nameof(RecorderOptions.FfmpegPath))] = Path.Combine(temp.Path, "missing-ffmpeg"),
        });

        var error = AssertStartupFails(factory);

        Assert.Contains("ffmpeg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_ffprobe_fails_startup_and_names_the_tool()
    {
        using var temp = new TempDirectory();
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [Setting(nameof(RecorderOptions.FfprobePath))] = Path.Combine(temp.Path, "missing-ffprobe"),
        });

        var error = AssertStartupFails(factory);

        Assert.Contains("ffprobe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_streamlink_fails_startup_and_names_the_tool()
    {
        using var temp = new TempDirectory();
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [Setting(nameof(RecorderOptions.StreamlinkPath))] =
                Path.Combine(temp.Path, "missing-streamlink"),
        });

        var error = AssertStartupFails(factory);

        Assert.Contains("streamlink", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_that_exits_non_zero_fails_startup_and_names_the_tool()
    {
        using var temp = new TempDirectory();
        string streamlink = CreateScript(
            temp,
            "streamlink-fails",
            """
            #!/bin/sh
            echo "streamlink cannot run here" >&2
            exit 7
            """);
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [Setting(nameof(RecorderOptions.StreamlinkPath))] = streamlink,
        });

        var error = AssertStartupFails(factory);

        Assert.Contains("streamlink", error.Message, StringComparison.Ordinal);
        Assert.Contains("code 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broken_fake_streamlink_passes_preflight_and_each_version_is_logged()
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [Setting(nameof(RecorderOptions.StreamlinkPath))] = FixturePath("fake-streamlink-broken"),
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(factory.Logs.Messages,
            message => message.StartsWith("ffmpeg: ffmpeg version", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Messages,
            message => message.StartsWith("ffprobe: ffprobe version", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Messages,
            message => message.Contains("streamlink 0.0.0-broken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preflight_probes_the_recordings_root()
    {
        using var temp = new TempDirectory();
        string blockedRoot = Path.Combine(temp.Path, "recordings");
        File.WriteAllText(blockedRoot, "not a directory");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewPreflight(temp, blockedRoot).StartAsync(CancellationToken.None));

        Assert.Contains("recordings root", error.Message, StringComparison.Ordinal);
        Assert.Contains("1654", error.Message, StringComparison.Ordinal);
        Assert.Contains("named volume", error.Message, StringComparison.Ordinal);
        Assert.Contains("chown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_probes_the_key_directory()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "recordings");
        Directory.CreateDirectory(Path.GetDirectoryName(AppDataPaths.KeysDirectory(root))!);
        File.WriteAllText(AppDataPaths.KeysDirectory(root), "not a directory");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewPreflight(temp, root).StartAsync(CancellationToken.None));

        Assert.Contains("Data Protection key directory", error.Message, StringComparison.Ordinal);
        Assert.Contains("1654", error.Message, StringComparison.Ordinal);
        Assert.Contains("chown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unwritable_recordings_root_fails_startup_with_ownership_guidance()
    {
        using var temp = new TempDirectory();
        string blockedRoot = Path.Combine(temp.Path, "recordings");
        File.WriteAllText(blockedRoot, "not a directory");
        using var factory = new StreamKeeprAppFactory(recordingsRoot: blockedRoot);

        var error = AssertStartupFails(factory);

        Assert.Contains("recordings root", error.Message, StringComparison.Ordinal);
        Assert.Contains("1654", error.Message, StringComparison.Ordinal);
        Assert.Contains("named volume", error.Message, StringComparison.Ordinal);
        Assert.Contains("chown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unwritable_key_directory_fails_startup_with_ownership_guidance()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "recordings");
        Directory.CreateDirectory(Path.GetDirectoryName(AppDataPaths.KeysDirectory(root))!);
        File.WriteAllText(AppDataPaths.KeysDirectory(root), "not a directory");
        using var factory = new StreamKeeprAppFactory(recordingsRoot: root);

        var error = AssertStartupFails(factory);

        Assert.Contains("Data Protection key directory", error.Message, StringComparison.Ordinal);
        Assert.Contains("1654", error.Message, StringComparison.Ordinal);
        Assert.Contains("chown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_is_the_first_hosted_service()
    {
        using var factory = new StreamKeeprAppFactory();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        Assert.IsType<ToolPreflight>(hosted[0]);
        int keyRingCheck = hosted.FindIndex(
            service => service.GetType().Name == "DataProtectionKeyRingStartupCheck");
        Assert.True(keyRingCheck > 0, "the key-ring check must start after preflight");
    }

    [Fact]
    public void Services_after_preflight_do_not_start_when_it_fails()
    {
        using var temp = new TempDirectory();
        var tracker = new TrackingHostedService();
        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                [Setting(nameof(RecorderOptions.StreamlinkPath))] =
                    Path.Combine(temp.Path, "missing-streamlink"),
            },
            configureTestServices: services => services.AddSingleton<IHostedService>(tracker));

        AssertStartupFails(factory);

        Assert.False(tracker.Started, "a later hosted service started after preflight failed");
    }

    private static ToolPreflight NewPreflight(TempDirectory temp, string recordingsRoot)
    {
        string streamlink = CreateScript(
            temp,
            "streamlink-version",
            """
            #!/bin/sh
            if [ "${1:-}" = "--version" ]; then
                echo "streamlink 0.0.0-fake"
                exit 0
            fi
            exit 1
            """);

        return new ToolPreflight(
            Options.Create(new RecorderOptions { StreamlinkPath = streamlink }),
            Options.Create(new StorageOptions { RecordingsRoot = recordingsRoot }),
            NullLogger<ToolPreflight>.Instance);
    }

    private static string Setting(string name) => $"{RecorderOptions.SectionName}:{name}";

    private static InvalidOperationException AssertStartupFails(StreamKeeprAppFactory factory)
    {
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException invalid)
                return invalid;
        }

        throw new Xunit.Sdk.XunitException(
            $"expected an InvalidOperationException in the startup failure, got {error}");
    }

    private static string CreateScript(TempDirectory temp, string name, string contents)
    {
        string path = Path.Combine(temp.Path, name);
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string FixturePath(string name)
    {
        string path = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", name);
        Assert.True(File.Exists(path), $"fixture script not found at {path}");

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            Assert.True(
                (mode & UnixFileMode.UserExecute) != 0,
                $"fixture script is not executable: {path}");
        }

        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StreamKeepr.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root from the test output");
    }

    private sealed class TrackingHostedService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "streamkeepr-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
