using dotenv.net;
using Microsoft.Extensions.Options;
using Polly.Telemetry;
using Serilog;
using StreamRecorder;
using StreamRecorder.Config;
using StreamRecorder.Configuration;
using StreamRecorder.Domain;
using StreamRecorder.Monitor;
using StreamRecorder.Recording;
using StreamRecorder.Storage;

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
           .AddSingleton<IValidateOptions<AppConfig>, AppConfigValidator>();

    builder.Services
           .AddOptions<TwitchOptions>()
           .Bind(builder.Configuration.GetSection(TwitchOptions.SectionName))
           .ValidateDataAnnotations()
           .ValidateOnStart();

    builder.Services
           .AddOptions<RecorderOptions>()
           .Bind(builder.Configuration.GetSection(RecorderOptions.SectionName))
           .ValidateDataAnnotations()
           .ValidateOnStart();

    builder.Services.AddSingleton<AppConfig>(sp =>
    {
        var twitchOpts = sp.GetRequiredService<IOptions<TwitchOptions>>();
        var recorderOpts = sp.GetRequiredService<IOptions<RecorderOptions>>();
        return new AppConfig(twitchOpts.Value, recorderOpts.Value);
    });

    builder.Services
           .AddSerilog((services, config) => config.ReadFrom.Configuration(builder.Configuration)
                                                   .ReadFrom.Services(services)
                                                   .Enrich.FromLogContext())
           .AddSingleton<IRecordingStore, RecordingStore>()
           .AddSingleton<IStreamInfoAccessor, StreamInfoAccessor>()
           .AddSingleton<IStreamInfoUpdater, StreamInfoUpdater>()
           .AddSingleton<IStreamCapturePipeline, StreamCapturePipeline>()
           .AddTwitchServices()
           .AddHostedService<StreamMonitorService>()
           .AddHostedService<StreamRecorderService>();

    builder.Services
           .Configure<TelemetryOptions>(options =>
           {
               options.SeverityProvider = args => args.Event.EventName switch
               {
                   "ExecutionAttempt" => ResilienceEventSeverity.Debug,
                   "OnRetry" => ResilienceEventSeverity.Debug,
                   _ => args.Event.Severity
               };
           })
           .Configure<HostOptions>(o =>
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
