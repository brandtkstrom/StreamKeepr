using Microsoft.Extensions.Options;
using Serilog;
using StreamRecorder;
using StreamRecorder.Infrastructure;
using StreamRecorder.Twitch;

Log.Logger = Logging.CreateBootstrapLogger();

try
{
    Log.Information("Starting stream recorder...");

    var builder = Host.CreateApplicationBuilder(args);

    var settings = builder.LoadRecorderSettings();

    // Configuration exists now, so swap in the real logger — console plus a rolling file
    // under RecorderSettings:RootPath — before anything resolves ILogger<T>. Both Log.Logger and
    // the DI registration point at the same instance so CloseAndFlushAsync flushes it.
    Serilog.ILogger logger = Logging.CreateLogger(settings);
    Log.Logger = logger;
    builder.Services.AddSerilog(logger);

    // Load & validate config
    builder.Services
           .AddSingleton<IValidateOptions<AppConfig>, AppConfigValidator>()
           .AddOptions<AppConfig>()
           .Bind(builder.Configuration)
           .ValidateOnStart();

    builder.Services
           .AddSerilog((services, config) => config.ReadFrom.Configuration(builder.Configuration)
                                                   .ReadFrom.Services(services)
                                                   .Enrich.FromLogContext())
           .AddTwitchServices()
           .AddHostedService<Worker>();

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