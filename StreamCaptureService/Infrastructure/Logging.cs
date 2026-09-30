using LanguageExt;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;

namespace StreamRecorder.Infrastructure;

/// <summary>Serilog wiring shared by the bootstrap and the configured logger.</summary>
public static class Logging
{
    /// <summary>
    /// Console-only logger for the window before configuration exists, so a failure while
    /// building the host is still reported. Replaced by <see cref="CreateLogger"/>.
    /// </summary>
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateLogger();

    /// <summary>
    /// Console plus a rolling file under <c>RecorderSettings:RootPath</c>, at the level from
    /// <c>RecorderSettings:LogLevel</c>. The log directory is created eagerly so an unwritable path
    /// fails loudly at startup instead of silently swallowing every subsequent write.
    /// </summary>
    public static Serilog.ILogger CreateLogger(this HostApplicationBuilder builder)
    {
        // Sink failures are otherwise silent — this is what made the old hardcoded path
        // fail invisibly. Surface them on stderr.
        SelfLog.Enable(Console.Error);

        using var serviceProvider = builder.Services.BuildServiceProvider();

        var config = serviceProvider.GetRequiredService<IOptions<AppConfig>>().Value;

        var logDirectory = Path.Combine(config.OutputPath, "logs");

        var level = ParseLogLevel(config.LogLevel).IfNone(() =>
        {
            Console.Error.WriteLine($"Unknown configured LogLevel '{config.LogLevel}'; using Information");
            return LogEventLevel.Information;
        });

        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
               .MinimumLevel.Is(level)
               .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
               .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
               .Enrich.FromLogContext()
               .WriteTo.Console()
               .WriteTo.File(path: Path.Combine(logDirectory, "recorder-.log"),
                             rollingInterval: RollingInterval.Day,
                             retainedFileCountLimit: 10,
                             fileSizeLimitBytes: 50_000_000,
                             rollOnFileSizeLimit: true,
                             shared: false)
               .CreateLogger();
    }

    // TODO - update
    /// <summary>
    /// Maps a configured level name to a Serilog level, case-insensitively. Accepts
    /// <c>verbose</c>/<c>trace</c>, <c>debug</c>, <c>info</c>/<c>information</c>,
    /// <c>warn</c>/<c>warning</c>, <c>error</c>, and <c>fatal</c>/<c>critical</c>; a missing
    /// name means Information, while an unknown name falls back to Information.
    /// </summary>
    private static Option<LogEventLevel> ParseLogLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "info" or "information" => LogEventLevel.Information,
            "verbose" or "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "warn" or "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" or "critical" => LogEventLevel.Fatal,
            var _ => Option<LogEventLevel>.None
        };
    }
}