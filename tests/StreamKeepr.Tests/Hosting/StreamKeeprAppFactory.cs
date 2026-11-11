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
    private readonly string _testRoot;
    private readonly bool _ownsTestRoot;
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
        _configureTestServices = configureTestServices;
        _settings = new()
        {
            [$"{TwitchOptions.SectionName}:ClientId"] = "test-client-id",
            [$"{TwitchOptions.SectionName}:ClientSecret"] = "test-client-secret",
            [$"{StorageOptions.SectionName}:RecordingsRoot"] = _testRoot,
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
    }
}
