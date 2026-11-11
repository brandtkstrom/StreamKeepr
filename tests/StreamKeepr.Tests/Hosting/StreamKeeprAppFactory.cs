using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamKeepr.Configuration;

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

    private readonly string _testRoot;
    private readonly bool _ownsTestRoot;
    private readonly string _toolsRoot;
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureTestServices;

    public StreamKeeprAppFactory(
        IReadOnlyDictionary<string, string?>? overrides = null,
        string? recordingsRoot = null,
        Action<IServiceCollection>? configureTestServices = null)
    {
        _ownsTestRoot = recordingsRoot is null;
        _testRoot = recordingsRoot ??
            Path.Combine(Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        _toolsRoot = Path.Combine(
            Path.GetTempPath(), "streamkeepr-test-tools", Guid.NewGuid().ToString("N"));
        _configureTestServices = configureTestServices;

        // CI installs ffmpeg but not Streamlink; preflight only needs a tool that
        // answers --version, so every host test gets a temp stand-in.
        Directory.CreateDirectory(_toolsRoot);
        string streamlink = Path.Combine(_toolsRoot, "streamlink");
        File.WriteAllText(streamlink, FakeStreamlinkScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                streamlink,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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
