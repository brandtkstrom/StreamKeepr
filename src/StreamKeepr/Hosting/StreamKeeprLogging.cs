using Serilog;
using Serilog.Extensions.Hosting;
using StreamKeepr.Mock;

namespace StreamKeepr.Hosting;

/// <summary>
/// Routes every <c>ILogger&lt;T&gt;</c> through Serilog with a single console sink,
/// configured from the <c>Serilog</c> section. Docker's <c>json-file</c> driver owns
/// retention, so no file sink exists.
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
    /// The first-stage logger: it covers startup before configuration is read, so
    /// fatal configuration and options failures still reach the console.
    /// </summary>
    public static ReloadableLogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .CreateBootstrapLogger();

    /// <summary>
    /// Mock and Testing hosts can start several times in one process under
    /// <c>WebApplicationFactory</c>. That factory runs the entry point itself and learns
    /// about a startup failure only from the exception the entry point throws.
    /// </summary>
    public static bool IsInProcessTestHost(IHostEnvironment environment) =>
        environment.IsEnvironment(TestingEnvironmentName) ||
        environment.IsEnvironment(MockEnvironment.Name);

    /// <summary>
    /// The second stage. Clears the framework providers so no default console line is
    /// written alongside Serilog's.
    /// </summary>
    /// <remarks>
    /// A bootstrap logger can be frozen only once, so in-process test hosts keep a private
    /// logger and leave the static one alone. Only Testing forwards to registered
    /// providers, so test assertions on <c>ListLoggerProvider</c> keep working without
    /// Mock writing each console line twice.
    /// </remarks>
    public static WebApplicationBuilder AddStreamKeeprLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(
            (services, logger) => logger
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext(),
            preserveStaticLogger: IsInProcessTestHost(builder.Environment),
            writeToProviders: builder.Environment.IsEnvironment(TestingEnvironmentName));

        return builder;
    }
}
