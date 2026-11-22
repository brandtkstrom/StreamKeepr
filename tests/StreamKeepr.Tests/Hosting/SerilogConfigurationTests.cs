using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;
using StreamKeepr.Hosting;

namespace StreamKeepr.Tests.Hosting;

public sealed partial class SerilogConfigurationTests
{
    public static TheoryData<string[]> ShippedProfiles => new()
    {
        { ["appsettings.json"] },
        { ["appsettings.json", "appsettings.Mock.json"] },
    };

    [Theory]
    [MemberData(nameof(ShippedProfiles))]
    public void Shipped_profiles_log_information_by_default_and_warnings_for_framework_categories(string[] files)
    {
        IConfiguration configuration = Load(files);

        Assert.Equal("Information", configuration["Serilog:MinimumLevel:Default"]);
        Assert.Equal("Warning", configuration["Serilog:MinimumLevel:Override:Microsoft"]);
        Assert.Equal("Information",
            configuration["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"]);
        Assert.Equal("Warning",
            configuration["Serilog:MinimumLevel:Override:System.Net.Http.HttpClient"]);
        Assert.False(configuration.GetSection("Logging").Exists());
    }

    [Theory]
    [MemberData(nameof(ShippedProfiles))]
    public void Shipped_profiles_cap_destructuring(string[] files)
    {
        IConfiguration configuration = Load(files);

        Assert.Equal("4", Destructure(configuration, "ToMaximumDepth", "maximumDestructuringDepth"));
        Assert.Equal("1024", Destructure(configuration, "ToMaximumStringLength", "maximumStringLength"));
        Assert.Equal("32", Destructure(configuration, "ToMaximumCollectionCount", "maximumCollectionCount"));
    }

    [Fact]
    public void Shipped_limits_truncate_oversized_strings_inside_destructured_objects()
    {
        var sink = new CapturingSink();
        using Logger logger = SinklessLogger(Load(["appsettings.json"]), sink);

        logger.Information("{@Payload}", new { Text = new string('x', 5000) });

        var payload = (StructureValue)sink.Events.Single().Properties["Payload"];
        string text = (string)((ScalarValue)payload.Properties.Single().Value).Value!;
        Assert.InRange(text.Length, 1, 1024);
    }

    [Theory]
    [MemberData(nameof(ShippedProfiles))]
    public void Shipped_profiles_configure_the_json_file_sink(string[] files)
    {
        IConfiguration configuration = Load(files);

        // Mock inherits Enabled=true; the environment gate, not this setting, keeps it file-free.
        Assert.True(configuration.GetValue("Serilog:File:Enabled", false));
        Assert.Equal(50_000_000L, configuration.GetValue("Serilog:File:FileSizeLimitBytes", 0L));
        Assert.Equal(7, configuration.GetValue("Serilog:File:RetainedFileCountLimit", 0));
    }

    [Fact]
    public void Development_profile_logs_at_debug_by_default()
    {
        IConfiguration configuration = Load(["appsettings.json", "appsettings.Development.json"]);

        Assert.Equal("Debug", configuration["Serilog:MinimumLevel:Default"]);
        Assert.Equal("Warning", configuration["Serilog:MinimumLevel:Override:Microsoft"]);
        Assert.Equal("Information",
            configuration["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"]);
    }

    [Fact]
    public void Console_sink_uses_the_same_template_as_the_bootstrap_logger()
    {
        IConfiguration configuration = Load(["appsettings.json"]);

        Assert.Equal("Console", configuration["Serilog:WriteTo:0:Name"]);
        Assert.Equal(StreamKeeprLogging.ConsoleTemplate,
            configuration["Serilog:WriteTo:0:Args:outputTemplate"]);
        Assert.False(configuration.GetSection("Serilog:WriteTo:1").Exists());
    }

    [Fact]
    public void Console_template_renders_timestamp_level_source_message_and_scope_properties()
    {
        var sink = new CapturingSink();
        using Logger serilog = SinklessLogger(Load(["appsettings.json"]), sink);
        using var factory = new SerilogLoggerFactory(serilog);
        Microsoft.Extensions.Logging.ILogger logger = factory.CreateLogger("StreamKeepr.Infrastructure.Runtime.ChannelCoordinator");

        using (logger.BeginScope(new Dictionary<string, object> { ["Channel"] = "somechannel" }))
            logger.LogInformation("Part {Part} started", 3);

        using var output = new StringWriter();
        new MessageTemplateTextFormatter(StreamKeeprLogging.ConsoleTemplate)
            .Format(sink.Events.Single(), output);
        string line = output.ToString();

        Assert.Matches(TimestampAndLevel(), line);
        Assert.Contains("StreamKeepr.Infrastructure.Runtime.ChannelCoordinator: Part 3 started", line);
        Assert.Contains("{\"Channel\":\"somechannel\"}", line);
    }

    private static IConfiguration Load(string[] files)
    {
        var builder = new ConfigurationBuilder();
        foreach (string file in files)
            builder.AddJsonFile(Path.Combine(TestPaths.RepositoryRoot(), "src", "StreamKeepr", file));

        return builder.Build();
    }

    private static string? Destructure(IConfiguration configuration, string name, string argument) =>
        configuration.GetSection("Serilog:Destructure").GetChildren()
            .Single(entry => entry["Name"] == name)[$"Args:{argument}"];

    /// <summary>
    /// The shipped pipeline minus its console sink, so tests observe events without
    /// writing to the shared process console.
    /// </summary>
    private static Logger SinklessLogger(IConfiguration configuration, ILogEventSink sink)
    {
        IConfiguration withoutSinks = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.AsEnumerable()
                .Where(entry => !entry.Key.StartsWith("Serilog:WriteTo", StringComparison.Ordinal)))
            .Build();

        return new LoggerConfiguration()
            .ReadFrom.Configuration(withoutSinks)
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
    }

    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} INF\] ")]
    private static partial Regex TimestampAndLevel();

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
