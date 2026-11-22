using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Extensions.Hosting;
using Serilog.Formatting.Compact;
using StreamKeepr.Configuration;
using StreamKeepr.Mock;

namespace StreamKeepr.Hosting;

/// <summary>
/// Routes every <c>ILogger&lt;T&gt;</c> through Serilog: the console sink configured by
/// the <c>Serilog</c> section plus, outside Testing and Mock, a compact-JSON file sink.
/// Docker's <c>json-file</c> driver owns console retention; the file sink keeps its own
/// bounded window under <c>.streamkeepr/logs</c> for history beyond that ring.
/// </summary>
internal static class StreamKeeprLogging
{
    /// <summary>The environment the integration-test host runs under.</summary>
    public const string TestingEnvironmentName = "Testing";

    /// <summary>
    /// The console line format. <c>appsettings.json</c> carries the same template for
    /// the configured pipeline; a test keeps the two in sync. <c>{Properties:j}</c>
    /// renders the structured property bag, including logging scopes.
    /// </summary>
    public const string ConsoleTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: " +
        "{Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// The section StreamKeepr reads directly for the JSON file sink. Serilog's
    /// configuration binder ignores it because the sink is added in code.
    /// </summary>
    public const string FileSection = "Serilog:File";

    /// <summary>A daily-rolled file; Serilog inserts the date before the extension.</summary>
    public const string FileName = "log-.json";

    private const long DefaultFileSizeLimitBytes = 50_000_000;
    private const int DefaultRetainedFileCountLimit = 7;

    /// <summary>
    /// The first-stage logger: it covers startup before configuration is read, so
    /// fatal configuration and options failures still reach the console.
    /// </summary>
    public static ReloadableLogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .CreateBootstrapLogger();

    /// <summary>
    /// True for the Testing and Mock environments. Both can start several times in one
    /// process under <c>WebApplicationFactory</c>, which runs the entry point itself and
    /// learns about a startup failure only from the exception the entry point throws.
    /// A standalone Mock run takes the same path.
    /// </summary>
    public static bool IsTestOrMockEnvironment(IHostEnvironment environment) =>
        environment.IsEnvironment(TestingEnvironmentName) ||
        environment.IsEnvironment(MockEnvironment.Name);

    /// <summary>
    /// The second stage. Clears the framework providers so no default console line is
    /// written alongside Serilog's.
    /// </summary>
    /// <remarks>
    /// A bootstrap logger can be frozen only once, so Testing and Mock hosts keep a private
    /// logger and leave the static one alone. Only Testing forwards to registered
    /// providers, so test assertions on <c>ListLoggerProvider</c> keep working without
    /// Mock writing each console line twice.
    /// </remarks>
    public static WebApplicationBuilder AddStreamKeeprLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        IConfiguration configuration = builder.Configuration;
        bool jsonFileLoggingAllowed = !IsTestOrMockEnvironment(builder.Environment);

        builder.Services.AddSerilog(
            (services, logger) =>
            {
                logger
                    .ReadFrom.Configuration(configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext();
                AddJsonFileSink(logger, configuration, jsonFileLoggingAllowed);
            },
            preserveStaticLogger: IsTestOrMockEnvironment(builder.Environment),
            writeToProviders: builder.Environment.IsEnvironment(TestingEnvironmentName));

        return builder;
    }

    /// <summary>
    /// Adds the compact-JSON file sink when it is enabled and the host may write files.
    /// A directory that cannot be created or written fails startup, like an unwritable
    /// recordings root.
    /// </summary>
    internal static void AddJsonFileSink(
        LoggerConfiguration logger,
        IConfiguration configuration,
        bool jsonFileLoggingAllowed)
    {
        if (!jsonFileLoggingAllowed || !configuration.GetValue($"{FileSection}:Enabled", true))
            return;

        string path = ResolveLogFilePath(configuration);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                $"The JSON log path '{path}' must include a directory.");

        // A configured path may be a mounted directory shared with other tools, so only
        // the default under .streamkeepr is treated as private app data and chmodded.
        bool privateAppData = string.IsNullOrWhiteSpace(configuration[$"{FileSection}:Path"]);

        try
        {
            if (privateAppData)
                OwnerOnlyFileSystem.EnsureWritablePrivateDirectory(directory);
            else
                OwnerOnlyFileSystem.EnsureWritableDirectory(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The JSON log directory '{directory}' is not writable. " +
                "Ensure the recordings volume is owned by the app user (UID 1654 in the container; " +
                "chown -R 1654:1654 <recordings>), or set 'Serilog:File:Path' to a writable directory.",
                ex);
        }

        logger.WriteTo.File(
            new CompactJsonFormatter(),
            path,
            fileSizeLimitBytes: configuration.GetValue(
                $"{FileSection}:FileSizeLimitBytes", DefaultFileSizeLimitBytes),
            buffered: false,
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: configuration.GetValue(
                $"{FileSection}:RetainedFileCountLimit", DefaultRetainedFileCountLimit));
    }

    /// <summary>
    /// The file Serilog opens: <c>Serilog:File:Path</c> when set, otherwise a daily-rolled
    /// file under the recordings volume's private app-data folder.
    /// </summary>
    internal static string ResolveLogFilePath(IConfiguration configuration)
    {
        string? configured = configuration[$"{FileSection}:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string? recordingsRoot = configuration[
            $"{StorageOptions.SectionName}:{nameof(StorageOptions.RecordingsRoot)}"];
        if (string.IsNullOrWhiteSpace(recordingsRoot))
            throw new InvalidOperationException(
                $"'{StorageOptions.SectionName}:{nameof(StorageOptions.RecordingsRoot)}' is required " +
                "to place JSON logs, or set 'Serilog:File:Path' to an explicit path.");

        return Path.Combine(AppDataPaths.LogsDirectory(recordingsRoot), FileName);
    }
}
