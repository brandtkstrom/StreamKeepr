using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Serilog.Extensions.Logging;
using StreamKeepr.Configuration;
using StreamKeepr.Mock;

namespace StreamKeepr.Tests.Hosting;

public sealed class SerilogHostTests
{
    private const string ProbeCategory = "StreamKeepr.Tests.SerilogProbe";

    [Fact]
    public void Testing_host_logs_through_serilog_and_still_feeds_the_test_provider()
    {
        using var factory = new StreamKeeprAppFactory();

        ILoggerFactory loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.CreateLogger(ProbeCategory).LogInformation("probe {Value}", 42);

        Assert.IsType<SerilogLoggerFactory>(loggerFactory);
        Assert.Contains("probe 42", factory.Logs.Messages);
    }

    [Fact]
    public void Logging_log_level_no_longer_controls_output()
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "None",
        });

        factory.Services.GetRequiredService<ILoggerFactory>()
               .CreateLogger(ProbeCategory)
               .LogInformation("still visible");

        Assert.Contains("still visible", factory.Logs.Messages);
    }

    [Fact]
    public void Serilog_minimum_level_override_filters_output()
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Override:StreamKeepr"] = "Warning",
        });

        ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>()
                                .CreateLogger(ProbeCategory);
        logger.LogInformation("filtered out");
        logger.LogWarning("kept");

        Assert.DoesNotContain("filtered out", factory.Logs.Messages);
        Assert.Contains("kept", factory.Logs.Messages);
    }

    [Fact]
    public void Testing_host_does_not_write_json_log_files()
    {
        using var factory = new StreamKeeprAppFactory();

        factory.Services.GetRequiredService<ILoggerFactory>()
               .CreateLogger(ProbeCategory)
               .LogInformation("probe");

        Assert.False(Directory.Exists(
            Path.Combine(factory.RecordingsRoot, ".streamkeepr", "logs")));
    }

    [Fact]
    public void Testing_host_registers_no_default_console_provider_that_would_duplicate_lines()
    {
        using var factory = new StreamKeeprAppFactory();

        IEnumerable<ILoggerProvider> providers =
            factory.Services.GetServices<ILoggerProvider>();

        Assert.DoesNotContain(providers, provider => provider is ConsoleLoggerProvider);
    }

    [Fact]
    public void In_process_hosts_never_touch_the_process_wide_static_logger()
    {
        using var testing = new StreamKeeprAppFactory();
        using var mock = new MockLoggingFactory();

        _ = testing.Services;
        _ = mock.Services;

        // Parallel test hosts share Serilog.Log; only the untouched silent default is safe.
        Assert.Equal("SilentLogger", Serilog.Log.Logger.GetType().Name);
    }

    [Fact]
    public void Mock_host_logs_through_serilog_without_writing_to_other_providers()
    {
        using var factory = new MockLoggingFactory();

        ILoggerFactory loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.CreateLogger(ProbeCategory).LogWarning("mock probe");

        Assert.IsType<SerilogLoggerFactory>(loggerFactory);
        Assert.DoesNotContain("mock probe", factory.Logs.Messages);
    }

    private sealed class MockLoggingFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

        public ListLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(MockEnvironment.Name);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<StorageOptions>(options => options.RecordingsRoot = _root);
                services.PostConfigure<MockSeedOptions>(options => options.Enabled = false);
                services.PostConfigure<MockLiveFeedOptions>(options => options.Enabled = false);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
