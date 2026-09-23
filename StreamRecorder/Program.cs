using dotenv.net;
using Microsoft.Extensions.Options;
using Serilog;
using StreamRecorder;
using StreamRecorder.Twitch;

Log.Logger = new LoggerConfiguration()
             .WriteTo.Console()
             .CreateLogger();
try
{
    Log.Information("Starting stream recorder...");

    DotEnv.Load(new DotEnvOptions(trimValues: true, overwriteExistingVars: true));

    var builder = Host.CreateApplicationBuilder(args);

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