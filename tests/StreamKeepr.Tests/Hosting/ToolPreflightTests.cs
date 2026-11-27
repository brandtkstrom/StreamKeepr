using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Storage;

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
            [Setting(nameof(RecorderOptions.StreamlinkPath))] = TestPaths.FixturePath("fake-streamlink-broken"),
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
    public async Task Preflight_probes_the_database_directory()
    {
        using var temp = new TempDirectory();
        string blocked = Path.Combine(temp.Path, "data");
        File.WriteAllText(blocked, "not a directory");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewPreflight(temp, databasePath: Path.Combine(blocked, "streamkeepr.db"))
                .StartAsync(CancellationToken.None));

        Assert.Contains("database directory", error.Message, StringComparison.Ordinal);
        Assert.Contains(blocked, error.Message, StringComparison.Ordinal);
        Assert.Contains("1654", error.Message, StringComparison.Ordinal);
        Assert.Contains("chown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_fails_when_the_database_volume_is_below_its_free_space_minimum()
    {
        using var temp = new TempDirectory();
        var disk = new FakeDiskSpace(freeBytes: 2 * DiskGuard.BytesPerGib, totalBytes: 100 * DiskGuard.BytesPerGib);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewPreflight(temp, disk: disk, databaseMinFreeGb: 3).StartAsync(CancellationToken.None));

        Assert.Contains("Storage:DatabaseMinFreeGb", error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(temp.Path, "data"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_passes_with_exactly_the_database_free_space_minimum()
    {
        using var temp = new TempDirectory();
        var disk = new FakeDiskSpace(freeBytes: 3 * DiskGuard.BytesPerGib, totalBytes: 100 * DiskGuard.BytesPerGib);

        await NewPreflight(temp, disk: disk, databaseMinFreeGb: 3).StartAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(temp.Path, "data")));
    }

    [Fact]
    public void Unwritable_database_directory_fails_startup_naming_it()
    {
        using var temp = new TempDirectory();
        string blocked = Path.Combine(temp.Path, "data");
        File.WriteAllText(blocked, "not a directory");
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [$"{StorageOptions.SectionName}:{nameof(StorageOptions.DatabasePath)}"] =
                Path.Combine(blocked, "streamkeepr.db"),
        });

        var error = AssertStartupFails(factory);

        Assert.Contains("database directory", error.Message, StringComparison.Ordinal);
        Assert.Contains(blocked, error.Message, StringComparison.Ordinal);
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
    public void Preflight_is_the_first_application_hosted_service()
    {
        using var factory = new StreamKeeprAppFactory(telemetry: true);

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        // OpenTelemetry inserts its hosted service at index 0 so it can capture early
        // telemetry. Preflight must remain the first application service after it.
        int preflight = hosted.FindIndex(service => service is ToolPreflight);
        Assert.True(preflight >= 0, "preflight must be registered");
        Assert.All(
            hosted.Take(preflight),
            service => Assert.StartsWith(
                "OpenTelemetry.", service.GetType().FullName, StringComparison.Ordinal));

        int keyRingCheck = hosted.FindIndex(
            service => service.GetType().Name == "DataProtectionKeyRingStartupCheck");
        Assert.True(keyRingCheck > preflight, "the key-ring check must start after preflight");
    }

    [Fact]
    public void Record_store_startup_runs_right_after_preflight_so_it_migrates_before_any_store_loads()
    {
        using var factory = new StreamKeeprAppFactory();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        int preflight = hosted.FindIndex(service => service is ToolPreflight);
        Assert.IsType<RecordStoreStartup>(hosted[preflight + 1]);
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

    private static ToolPreflight NewPreflight(
        TempDirectory temp,
        string? recordingsRoot = null,
        string? databasePath = null,
        IDiskSpace? disk = null,
        int databaseMinFreeGb = 1)
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
            Options.Create(new StorageOptions
            {
                RecordingsRoot = recordingsRoot ?? Path.Combine(temp.Path, "recordings"),
                DatabasePath = databasePath ?? Path.Combine(temp.Path, "data", "streamkeepr.db"),
                DatabaseMinFreeGb = databaseMinFreeGb,
            }),
            disk ?? new FakeDiskSpace(100 * DiskGuard.BytesPerGib, 200 * DiskGuard.BytesPerGib),
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
        return TestPaths.StartableScriptPath(path);
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

}
