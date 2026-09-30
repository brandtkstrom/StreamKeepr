using dotenv.net;
using Microsoft.Extensions.Options;
using Serilog;
using StreamRecorder;
using StreamRecorder.Infrastructure;
using StreamRecorder.Services;
using StreamRecorder.Twitch;

Log.Logger = Logging.CreateBootstrapLogger();

try
{
    Log.Information("Starting stream recorder...");

    // Local development convenience: pull .env into the process environment before the host
    // builds configuration, so the standard environment-variable provider sees the values.
    // Real environment variables and command-line arguments take precedence.
    if (File.Exists(".env"))
    {
        Log.Debug("Loading environment variables from .env file");
        DotEnv.Load();
    }

    var builder = Host.CreateApplicationBuilder(args);

    Serilog.ILogger logger = builder.LoadAppConfig().CreateLogger();
    Log.Logger = logger;

    builder.Services
           .AddSerilog(logger)
           .AddTwitchServices()
           .AddSingleton<StreamInfoAccessor>();

    builder.Services.Configure<HostOptions>(o =>
    {
        o.ShutdownTimeout = TimeSpan.FromSeconds(30);
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    });

    var host = builder.Build();
    host.Run();
}
catch (OptionsValidationException ex)
{
    Log.Fatal("Invalid configuration: {Message}", ex.Failures);
}
catch (OperationCanceledException)
{
    Log.Information("Stream recorder shutting down");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Stream recorder terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}