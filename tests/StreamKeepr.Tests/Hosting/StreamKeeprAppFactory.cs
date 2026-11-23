using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Hosting;

/// <summary>
/// Hosts the real app with test-only configuration and a temp recordings root.
/// </summary>
internal sealed class StreamKeeprAppFactory : WebApplicationFactory<Program>
{
    private const string FakeStreamlinkScript = """
        #!/bin/sh
        if [ "${1:-}" = "--version" ]; then
            echo "streamlink 0.0.0-fake"
            exit 0
        fi
        echo "fake streamlink: only --version is supported" >&2
        exit 1
        """;

    private static readonly TimeSpan StartupRecoveryTimeout = TimeSpan.FromSeconds(30);

    private readonly string _testRoot;
    private readonly bool _ownsTestRoot;
    private readonly string _toolsRoot;
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly bool _telemetry;

    public StreamKeeprAppFactory(
        IReadOnlyDictionary<string, string?>? overrides = null,
        string? recordingsRoot = null,
        Action<IServiceCollection>? configureTestServices = null,
        bool telemetry = false)
    {
        _ownsTestRoot = recordingsRoot is null;
        _testRoot = recordingsRoot ??
            Path.Combine(Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        _toolsRoot = Path.Combine(
            Path.GetTempPath(), "streamkeepr-test-tools", Guid.NewGuid().ToString("N"));
        _configureTestServices = configureTestServices;
        _telemetry = telemetry;

        // CI installs ffmpeg but not Streamlink; preflight only needs a tool that
        // answers --version, so every host test gets a temp stand-in.
        Directory.CreateDirectory(_toolsRoot);
        string streamlinkScript = Path.Combine(_toolsRoot, "streamlink");
        File.WriteAllText(streamlinkScript, FakeStreamlinkScript);
        string streamlink = TestPaths.StartableScriptPath(streamlinkScript);

        _settings = new()
        {
            [$"{TwitchOptions.SectionName}:ClientId"] = "test-client-id",
            [$"{TwitchOptions.SectionName}:ClientSecret"] = "test-client-secret",
            [$"{StorageOptions.SectionName}:RecordingsRoot"] = _testRoot,
            [$"{RecorderOptions.SectionName}:StreamlinkPath"] = streamlink,
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            _settings[key] = value;
    }

    public string RecordingsRoot => _settings[$"{StorageOptions.SectionName}:RecordingsRoot"]!;

    public ListLoggerProvider Logs { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Program reads Telemetry:Enabled while the app is being built, before the
        // WebApplicationFactory app-configuration callbacks run. Host configuration is
        // applied earlier, so every test is explicit without mutating the process.
        builder.ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = _telemetry ? "true" : "false",
            }));

        IHost host = base.CreateHost(builder);
        WaitForStartupRecovery(host);
        return host;
    }

    /// <summary>
    /// Startup recovery runs after the server starts listening (DvrSupervisor.ExecuteAsync), and
    /// its library scan replaces the library's contents. Returning before it finishes would let
    /// a test seed or open a recording that the scan then wipes. Waits until the library is
    /// loaded, or until the host stops itself: a failed recovery never loads it.
    /// </summary>
    private static void WaitForStartupRecovery(IHost host)
    {
        RecordingLibrary library = host.Services.GetRequiredService<RecordingLibrary>();
        CancellationToken stopping =
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged()
        {
            if (library.IsLoaded)
                settled.TrySetResult();
        }

        library.Changed += OnChanged;
        try
        {
            // Covers a scan that finished before the subscription.
            OnChanged();
            using CancellationTokenRegistration onStopping =
                stopping.Register(() => settled.TrySetResult());

            if (!settled.Task.Wait(StartupRecoveryTimeout))
            {
                throw new TimeoutException(
                    $"Startup recovery did not load the recording library within {StartupRecoveryTimeout}.");
            }
        }
        finally
        {
            library.Changed -= OnChanged;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(StreamKeeprLogging.TestingEnvironmentName);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        if (_configureTestServices is not null)
            builder.ConfigureTestServices(_configureTestServices);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (_ownsTestRoot && Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        if (Directory.Exists(_toolsRoot))
            Directory.Delete(_toolsRoot, recursive: true);
    }
}
