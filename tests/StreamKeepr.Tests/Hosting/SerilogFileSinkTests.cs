using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using StreamKeepr.Hosting;

namespace StreamKeepr.Tests.Hosting;

public sealed class SerilogFileSinkTests
{
    [Fact]
    public void Default_path_is_the_private_app_data_logs_folder()
    {
        using var root = new TempDirectory();

        string path = StreamKeeprLogging.ResolveLogFilePath(Config(root.Path));

        Assert.Equal(
            Path.Combine(root.Path, ".streamkeepr", "logs", StreamKeeprLogging.FileName),
            path);
    }

    [Fact]
    public void Configured_path_wins()
    {
        using var root = new TempDirectory();
        string custom = Path.Combine(root.Path, "elsewhere", "app-.json");

        string path = StreamKeeprLogging.ResolveLogFilePath(
            Config(root.Path, ("Serilog:File:Path", custom)));

        Assert.Equal(custom, path);
    }

    [Fact]
    public void Missing_recordings_root_names_the_setting()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => StreamKeeprLogging.ResolveLogFilePath(configuration));

        Assert.Contains("Storage:RecordingsRoot", error.Message);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Disabled_or_ineligible_file_logging_writes_nothing(bool jsonFileLoggingAllowed, bool enabled)
    {
        using var root = new TempDirectory();
        var loggerConfiguration = new LoggerConfiguration();

        StreamKeeprLogging.AddJsonFileSink(
            loggerConfiguration,
            Config(root.Path, ("Serilog:File:Enabled", enabled ? "true" : "false")),
            jsonFileLoggingAllowed);

        using (var logger = loggerConfiguration.CreateLogger())
            logger.Information("not written");

        Assert.False(Directory.Exists(Path.Combine(root.Path, ".streamkeepr", "logs")));
    }

    [Fact]
    public void File_sink_writes_one_compact_json_object_per_line()
    {
        using var root = new TempDirectory();
        var loggerConfiguration = new LoggerConfiguration();

        StreamKeeprLogging.AddJsonFileSink(
            loggerConfiguration, Config(root.Path), jsonFileLoggingAllowed: true);

        using (var logger = loggerConfiguration.CreateLogger())
        {
            logger.ForContext("Channel", "somechannel").Information("Part {Part} started", 3);
            logger.Error(new InvalidOperationException("boom"), "Recorder stopped.");
        }

        string logs = Path.Combine(root.Path, ".streamkeepr", "logs");
        string[] lines = Directory.EnumerateFiles(logs, "*.json")
            .Order(StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .ToArray();

        Assert.Equal(2, lines.Length);

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Part {Part} started", first.RootElement.GetProperty("@mt").GetString());
        // CLEF omits @l for Information, the default level.
        Assert.False(first.RootElement.TryGetProperty("@l", out _));
        Assert.Equal("somechannel", first.RootElement.GetProperty("Channel").GetString());
        Assert.True(first.RootElement.TryGetProperty("@t", out _));

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Recorder stopped.", second.RootElement.GetProperty("@mt").GetString());
        Assert.Equal("Error", second.RootElement.GetProperty("@l").GetString());
        Assert.Contains("boom", second.RootElement.GetProperty("@x").GetString()!);
    }

    [Fact]
    public void Daily_rolled_file_names_keep_the_json_extension()
    {
        using var root = new TempDirectory();
        var loggerConfiguration = new LoggerConfiguration();
        StreamKeeprLogging.AddJsonFileSink(
            loggerConfiguration, Config(root.Path), jsonFileLoggingAllowed: true);

        using (var logger = loggerConfiguration.CreateLogger())
            logger.Information("opens the dated file");

        string[] files = Directory.GetFiles(Path.Combine(root.Path, ".streamkeepr", "logs"), "*.json");
        Assert.Single(files);
        Assert.Matches(@"^log-\d{8}\.json$", Path.GetFileName(files[0]));
    }

    [Fact]
    public void Configured_path_is_used_by_the_sink()
    {
        using var root = new TempDirectory();
        string directory = Path.Combine(root.Path, "custom");
        string path = Path.Combine(directory, "app-.json");
        var loggerConfiguration = new LoggerConfiguration();

        StreamKeeprLogging.AddJsonFileSink(
            loggerConfiguration,
            Config(root.Path, ("Serilog:File:Path", path)),
            jsonFileLoggingAllowed: true);

        using (var logger = loggerConfiguration.CreateLogger())
            logger.Information("written");

        string[] files = Directory.GetFiles(directory, "*.json");
        Assert.Single(files);
        Assert.StartsWith("app-", Path.GetFileName(files[0]));
    }

    [Fact]
    public void Bare_file_name_is_rejected_with_a_clear_message()
    {
        using var root = new TempDirectory();

        var error = Assert.Throws<InvalidOperationException>(() =>
            StreamKeeprLogging.AddJsonFileSink(
                new LoggerConfiguration(),
                Config(root.Path, ("Serilog:File:Path", "log-.json")),
                jsonFileLoggingAllowed: true));

        Assert.Contains("must include a directory", error.Message);
    }

    [Fact]
    public void Unwritable_log_directory_fails_with_the_path_in_the_message()
    {
        using var root = new TempDirectory();
        string blocked = Path.Combine(root.Path, "blocked");
        File.WriteAllText(blocked, "not a directory");

        var error = Assert.Throws<InvalidOperationException>(() =>
            StreamKeeprLogging.AddJsonFileSink(
                new LoggerConfiguration(),
                Config(root.Path, ("Serilog:File:Path", Path.Combine(blocked, "log-.json"))),
                jsonFileLoggingAllowed: true));

        Assert.Contains("not writable", error.Message);
        Assert.Contains(blocked, error.Message);
    }

    [Fact]
    public void Size_limit_rolls_files_and_retention_keeps_the_newest()
    {
        using var root = new TempDirectory();
        var loggerConfiguration = new LoggerConfiguration();

        StreamKeeprLogging.AddJsonFileSink(
            loggerConfiguration,
            Config(
                root.Path,
                ("Serilog:File:FileSizeLimitBytes", "1024"),
                ("Serilog:File:RetainedFileCountLimit", "2")),
            jsonFileLoggingAllowed: true);

        using (var logger = loggerConfiguration.CreateLogger())
        {
            for (int i = 0; i < 40; i++)
                logger.Information("Event {Index} {Payload}", i, new string('x', 200));
        }

        string[] files = Directory.GetFiles(
            Path.Combine(root.Path, ".streamkeepr", "logs"), "*.json");
        Assert.Equal(2, files.Length);
    }

    private static IConfiguration Config(
        string recordingsRoot, params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Storage:RecordingsRoot"] = recordingsRoot,
        };
        foreach (var (key, value) in overrides)
            values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
