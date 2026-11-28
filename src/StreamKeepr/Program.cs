using System.Diagnostics.Metrics;
using MudBlazor.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Extensions.Hosting;
using StreamKeepr.Components;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Telemetry;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Infrastructure.Twitch.Chat;
using StreamKeepr.Mock;
using StreamKeepr.Ui;

// Two-stage Serilog bootstrap: this logger covers startup until configuration is read,
// so configuration and options-validation failures are logged before the process exits.
ReloadableLogger bootstrapLogger = StreamKeeprLogging.CreateBootstrapLogger();
bool testOrMockHost = false;

try
{
    var builder = WebApplication.CreateBuilder(args);
    testOrMockHost = StreamKeeprLogging.IsTestOrMockEnvironment(builder.Environment);

    // Only a real host publishes the process-wide logger; AddSerilog then freezes it into
    // the configured pipeline. Test and Mock hosts share a process, so they keep it local.
    if (!testOrMockHost)
        Log.Logger = bootstrapLogger;

    builder.AddStreamKeeprLogging();

    // MapStaticAssets serves build-time assets, but the framework only installs their
    // file provider in Development. Mock/Testing run from build output and need it;
    // published Production serves wwwroot directly.
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsProduction())
        StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

    // Compose mounts secret files at /run/secrets. AddKeyPerFile maps "__" in a
    // file name to ":" in the key (Twitch__ClientId becomes Twitch:ClientId), so
    // secrets bind exactly like the matching environment variables. This runs
    // before the Mock profile adds its in-memory credentials, so Mock stays fake.
    if (Directory.Exists("/run/secrets"))
        builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

    // The Mock profile is a disposable local/E2E host: it forces dummy credentials and a
    // recordings root under the system temp directory before any options are bound.
    bool mock = builder.Environment.IsEnvironment(MockEnvironment.Name);
    if (mock)
    {
        MockEnvironment.Configure(builder);
    }

    // Telemetry is opt-in and never sends anything unless an OTLP endpoint is configured.
    // The Mock in-memory configuration runs last, so the disposable profile can never inherit
    // a real endpoint from the host environment.
    bool telemetryEnabled = builder.Configuration.GetValue("Telemetry:Enabled", false);
    if (telemetryEnabled && TelemetryQueryRedaction.DisableRequested(builder.Configuration))
    {
        throw new InvalidOperationException(
            "OpenTelemetry URL query redaction cannot be disabled; OAuth codes and state must never be exported.");
    }

    // The singleton exists even when telemetry is off: it reports Enabled=false, its
    // instruments are no-ops without a provider, and instrumented constructors resolve it.
    builder.Services.AddSingleton(services => new StreamKeeprTelemetry(
        services.GetRequiredService<IMeterFactory>(), telemetryEnabled));

    if (telemetryEnabled)
    {
        string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        bool export = ParseEndpoint(otlpEndpoint) is not null;
        int exportTimeout = builder.Configuration.GetValue(
            "OTEL_EXPORTER_OTLP_TIMEOUT", TelemetryDefaults.ExportTimeoutMilliseconds);
        int batchTimeout = builder.Configuration.GetValue(
            "OTEL_BSP_EXPORT_TIMEOUT", TelemetryDefaults.BatchExportTimeoutMilliseconds);
        int batchDelay = builder.Configuration.GetValue(
            "OTEL_BSP_SCHEDULE_DELAY", TelemetryDefaults.BatchScheduleDelayMilliseconds);
        int metricTimeout = builder.Configuration.GetValue(
            "OTEL_METRIC_EXPORT_TIMEOUT", TelemetryDefaults.MetricExportTimeoutMilliseconds);
        int metricInterval = builder.Configuration.GetValue(
            "OTEL_METRIC_EXPORT_INTERVAL", TelemetryDefaults.MetricExportIntervalMilliseconds);

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    StreamKeeprTelemetry.ServiceName,
                    serviceVersion: StreamKeeprTelemetry.ServiceVersion,
                    autoGenerateServiceInstanceId: false)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = TelemetryEnvironment.Name(builder.Environment),
                }));

        openTelemetry.WithTracing(tracing =>
        {
            tracing.AddSource(StreamKeeprTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = TelemetryPaths.ShouldTrace;
                    options.EnableAspNetCoreSignalRSupport = false;
                    options.EnableRazorComponentsSupport = false;
                    options.EnrichWithHttpResponse = TelemetryPaths.RewriteUrlPath;
                })
                .AddHttpClientInstrumentation();

            if (export)
            {
                tracing.AddOtlpExporter(options =>
                {
                    options.TimeoutMilliseconds = exportTimeout;
                    options.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = batchTimeout;
                    options.BatchExportProcessorOptions.ScheduledDelayMilliseconds = batchDelay;
                });
            }
        });

        openTelemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter(StreamKeeprTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("System.Runtime");

            if (export)
            {
                metrics.AddOtlpExporter((options, reader) =>
                {
                    options.TimeoutMilliseconds = exportTimeout;
                    reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = metricTimeout;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = metricInterval;
                });
            }
        });

        builder.Services.AddSingleton<StreamKeeprRuntimeMetrics>();
    }

    // The host budget is the recorder's stop budget plus shutdown/cleanup headroom.
    TimeSpan hostShutdownHeadroom = TimeSpan.FromSeconds(5);
    builder.Services.AddOptions<HostOptions>()
           .Configure<IRecorder>((host, recorder) =>
               host.ShutdownTimeout = recorder.StopBudget + hostShutdownHeadroom);

    builder.Services.AddOptions<TwitchOptions>()
           .BindConfiguration(TwitchOptions.SectionName)
           .ValidateDataAnnotations()
           .Validate(options => options.Channels.All(AuthOptions.IsValidLogin),
                     $"{TwitchOptions.SectionName}:{nameof(TwitchOptions.Channels)} contains an invalid Twitch login.")
           .ValidateOnStart();

    builder.Services.AddOptions<RecorderOptions>()
           .BindConfiguration(RecorderOptions.SectionName)
           .ValidateDataAnnotations()
           .ValidateOnStart();

    builder.Services.AddOptions<StorageOptions>()
           .BindConfiguration(StorageOptions.SectionName)
           .ValidateDataAnnotations()
           .ValidateOnStart();

    builder.Services.AddOptions<LivePlaylistOptions>()
           .BindConfiguration(LivePlaylistOptions.SectionName)
           .ValidateDataAnnotations()
           .ValidateOnStart();

    builder.Services.AddOptions<AuthOptions>()
           .BindConfiguration(AuthOptions.SectionName)
           .ValidateDataAnnotations()
           .Validate(options => options.AllowedTwitchLogins.All(AuthOptions.IsValidLogin),
                     $"{AuthOptions.SectionName}:{nameof(AuthOptions.AllowedTwitchLogins)} contains an invalid Twitch login.")
           .ValidateOnStart();

    builder.Services.AddLinksOptions();

    builder.Services.AddOptions<ChatOptions>()
           .BindConfiguration(ChatOptions.SectionName)
           .ValidateDataAnnotations()
           .Validate(options => !options.HasToken || TwitchLogin.IsValid(
                       TwitchLogin.Normalize(options.TwitchLogin)),
                     $"{ChatOptions.SectionName}:{nameof(ChatOptions.TwitchLogin)} must be a valid login when a chat token is set.")
           .ValidateOnStart();

    if (mock)
    {
        builder.Services.AddOptions<MockSeedOptions>()
               .BindConfiguration(MockSeedOptions.SectionName)
               .ValidateDataAnnotations()
               .ValidateOnStart();
    }

    builder.Services.AddStreamKeeprForwardedHeaders();

    builder.Services.AddSingleton(TimeProvider.System);

    // The record store (ADR-0014): one SQLite database at Storage:DatabasePath, migrated by
    // RecordStoreStartup before any store loads.
    builder.Services.AddStreamKeeprRecordStore();

    // The session store owns the session records, index.m3u8, and the per-session locks.
    builder.Services.AddSingleton<RecordingLibrary>();
    builder.Services.AddSingleton<IDiskSpace, DriveInfoDiskSpace>();
    builder.Services.AddSingleton<DiskGuard>();
    builder.Services.AddSingleton<IStorageMonitor, StorageMonitor>();
    builder.Services.AddSingleton<Func<TimeSpan, CancellationToken, Task>>(
        _ => static (delay, ct) => Task.Delay(delay, ct));
    builder.Services.AddSingleton<SessionStore>();
    // The live index is refreshed off the request path; requests only read its published bodies.
    builder.Services.AddSingleton<IPartPlaylistSource>(FilePartPlaylistSource.Instance);
    builder.Services.AddSingleton<LiveIndex>();
    builder.Services.AddSingleton<LiveIndexRefresher>();
    builder.Services.AddSingleton<ChannelStore>();
    builder.Services.AddSingleton<ProgressStore>();

    // Chat capture is a best-effort read-only tap; it never participates in recording decisions.
    builder.Services.AddSingleton<ChatLogStore>();
    builder.Services.AddSingleton<ChatReader>();
    builder.Services.AddSingleton<TwitchChatParser>();
    builder.Services.AddSingleton<EmoteChannelResolver>();
    builder.Services.AddSingleton<IEmoteChannelResolver>(
        services => services.GetRequiredService<EmoteChannelResolver>());
    builder.Services.AddSingleton<EmoteCatalogService>();
    builder.Services.AddSingleton<BadgeCatalogService>();
    builder.Services.AddSingleton<IExportLeaseRegistry, ExportLeaseRegistry>();
    builder.Services.AddSingleton<IRecordingAdmin, RecordingAdmin>();
    builder.Services.AddSingleton<ExportService>();
    builder.Services.AddSingleton<IExportService>(services => services.GetRequiredService<ExportService>());
    builder.Services.AddSingleton<IProgressStore>(services => services.GetRequiredService<ProgressStore>());
    builder.Services.AddSingleton<RecoveryService>();
    builder.Services.AddSingleton<ThumbnailService>();
    builder.Services.AddSingleton<RecorderJob>();
    builder.Services.AddSingleton<IRecorder>(services => services.GetRequiredService<RecorderJob>());

    // Runtime: one coordinator per stored channel, fed by the Helix poller. The Mock profile
    // swaps in deterministic fakes and never constructs the Twitch HTTP/EventSub clients.
    builder.Services.AddSingleton<IRetryJitter, RandomizedRetryJitter>();
    builder.Services.AddSingleton<DvrStatus>();
    builder.Services.AddSingleton<ChannelCoordinatorFactory>();
    builder.Services.AddSingleton<PollSchedule>();

    if (mock)
    {
        builder.Services.AddSingleton<MockTwitch>();
        builder.Services.AddSingleton<IStreamPoller, MockStreamPoller>();
        builder.Services.AddSingleton<IEventSubClient, MockEventSubClient>();
        builder.Services.AddSingleton<MockChatSource>();
        builder.Services.AddSingleton<IChatSource>(services =>
            services.GetRequiredService<MockChatSource>());

        // The fixed fixture keeps the Mock profile off both BetterTTV endpoints.
        builder.Services.AddSingleton<IEmoteProvider, MockEmoteProvider>();
        // Badges likewise use a fixed catalog and the reserved .invalid image host.
        builder.Services.AddSingleton<IBadgeSource, MockBadgeSource>();

        // The mock volume makes the 1 % storage threshold observable in local/E2E runs.
        builder.Services.AddSingleton<MockDiskSpace>();

        builder.Services.AddSingleton<IDiskSpace>(services =>
                                                      services.GetRequiredService<MockDiskSpace>());

        builder.Services.AddOptions<MockLiveFeedOptions>()
               .BindConfiguration(MockLiveFeedOptions.SectionName)
               .ValidateOnStart();
    }
    else
    {
        builder.Services.AddSingleton<TwitchTokenProvider>();
        builder.Services.AddSingleton<IStreamPoller, TwitchStreamPoller>();
        builder.Services.AddSingleton<IEventSubSocketFactory, ClientWebSocketEventSubSocketFactory>();

        // Twitch:EventSubTransport picks the push transport (ADR-0007); polling covers both.
        builder.Services.AddSingleton<IConduitStateStore, ConduitStateStore>();
        builder.Services.AddSingleton<EventSubConduitApi>();
        builder.Services.AddSingleton<IEventSubBinding>(services =>
            services.GetRequiredService<IOptions<TwitchOptions>>().Value.EventSubTransport
                == EventSubTransportMode.Conduit
                ? ActivatorUtilities.CreateInstance<ConduitEventSubBinding>(services)
                : ActivatorUtilities.CreateInstance<WebSocketEventSubBinding>(services));
        builder.Services.AddSingleton<IEventSubClient, EventSubClient>();

        builder.Services.AddSingleton<IIrcSocketFactory, ClientWebSocketIrcSocketFactory>();
        builder.Services.AddSingleton<IrcChatClient>();
        builder.Services.AddSingleton<IChatSource>(services =>
            services.GetRequiredService<IrcChatClient>());

        builder.Services.AddSingleton<IEmoteProvider, BttvEmoteProvider>();
        builder.Services.AddSingleton<IEmoteProvider, SevenTvEmoteProvider>();
        builder.Services.AddSingleton<IEmoteProvider, FfzEmoteProvider>();
        builder.Services.AddSingleton<IBadgeSource, TwitchBadgeSource>();
    }

    builder.Services.AddSingleton<DvrSupervisor>();
    builder.Services.AddSingleton<IChannelControl>(services => services.GetRequiredService<DvrSupervisor>());

    // Hosted services start in registration order, so preflight runs before Data
    // Protection, the supervisor, and any channel or recording service.
    builder.Services.AddHostedService<ToolPreflight>();
    // Opens, checks, backs up and migrates the record store; every store loads after it.
    builder.Services.AddHostedService<RecordStoreStartup>();

    if (mock)
    {
        // Seeding is split by store; the viewer and chat seeders read what MockSeeder recorded.
        builder.Services.AddHostedService<MockSeeder>();
        builder.Services.AddHostedService<MockLiveFeed>();
        builder.Services.AddHostedService<MockViewerSeeder>();
        builder.Services.AddHostedService<MockChatSeeder>();
    }

    // Loads resume positions from the record store; shutdown waits out a write in flight.
    builder.Services.AddHostedService(services => services.GetRequiredService<ProgressStore>());
    // One FFmpeg export at a time, independent of any circuit.
    builder.Services.AddHostedService(services => services.GetRequiredService<ExportService>());

    // Keys live beside the record store and are ready before auth or token storage runs.
    builder.Services.AddStreamKeeprDataProtection();

    // The supervisor starts only after preflight and the key-ring check have succeeded.
    builder.Services.AddHostedService(services => services.GetRequiredService<DvrSupervisor>());
    // Refreshes the live index after the supervisor's recovery has loaded the library.
    builder.Services.AddHostedService(services => services.GetRequiredService<LiveIndexRefresher>());
    // Grabs thumbnails from recorded media; registered after the refresher it reads from.
    builder.Services.AddHostedService(services => services.GetRequiredService<ThumbnailService>());

    // Start the chat source before the capture reconciler; both are non-fatal to recording.
    if (mock)
        builder.Services.AddHostedService(services => services.GetRequiredService<MockChatSource>());
    else
        builder.Services.AddHostedService(services => services.GetRequiredService<IrcChatClient>());

    builder.Services.AddHostedService<ChatCaptureService>();

    // Conduit rollback cleanup; a no-op unless WebSocket mode finds a saved conduit.
    if (!mock)
        builder.Services.AddHostedService<ConduitRetirementService>();

    builder.Services.AddStreamKeeprHostLifecycleLog();

    builder.Services.AddHealthChecks()
           .AddCheck<DvrHealthCheck>(DvrHealthCheck.Name, tags: ["live", "ready"])
           .AddCheck<DiskSpaceHealthCheck>("disk", tags: ["ready"])
           .AddCheck<LibraryLoadedHealthCheck>("library", tags: ["ready"])
           .AddCheck<RecordStoreHealthCheck>(RecordStoreHealthCheck.Name, tags: ["ready"]);

    builder.Services.AddHttpClient(TwitchHttpClients.Auth, client =>
                                       client.BaseAddress = new Uri("https://id.twitch.tv/"));

    builder.Services.AddHttpClient(TwitchHttpClients.Helix, client =>
                                       client.BaseAddress = new Uri("https://api.twitch.tv/"));

    builder.Services.AddHttpClient(EmoteHttpClients.Bttv, client =>
                                       client.BaseAddress = new Uri(EmoteHttpClients.BttvBaseAddress));

    builder.Services.AddHttpClient(EmoteHttpClients.SevenTv, client =>
                                       client.BaseAddress = new Uri(EmoteHttpClients.SevenTvBaseAddress));

    builder.Services.AddHttpClient(EmoteHttpClients.Ffz, client =>
                                       client.BaseAddress = new Uri(EmoteHttpClients.FfzBaseAddress));

    builder.Services.AddSingleton<IAdminTokenStore, AdminTokenStore>();

    if (mock)
    {
        builder.Services.AddStreamKeeprMockAuth();
        builder.Services.AddSingleton<IAdminSessionValidator, AlwaysValidAdminSessionValidator>();
    }
    else
    {
        builder.Services.AddStreamKeeprAdminAuth();
        builder.Services.AddSingleton<IAdminSessionValidator, TwitchAdminSessionValidator>();
    }

    builder.Services.AddAuthorization();
    builder.Services.AddStreamKeeprPrivateMode();
    builder.Services.AddCascadingAuthenticationState();

    // Re-checks the admin's Twitch token every 30 minutes; on failure the circuit signs out
    // and the top bar shows the expired state while playback continues.
    builder.Services.AddScoped<AdminSessionState>();
    builder.Services.AddScoped<AuthenticationStateProvider, AdminAuthenticationStateProvider>();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
    builder.Services.AddScoped<UiPreferences>();
    builder.Services.AddScoped<ViewerClock>();
    builder.Services.AddScoped<PlayerBridge>();
    builder.Services.AddScoped<LibraryView>();
    builder.Services.AddScoped<ILibraryView>(services => services.GetRequiredService<LibraryView>());
    builder.Services.AddScoped<IChannelsView, ChannelsView>();
    builder.Services.AddScoped<IConnectionStatusView, ConnectionStatusView>();

    builder.Services.AddRazorComponents()
           .AddInteractiveServerComponents();
    builder.Services.AddMudServices(options => StreamKeeprSnackbars.Configure(options.SnackbarConfiguration));

    var app = builder.Build();

    if (mock)
        MockEnvironment.DeleteRecordStoreOnCleanShutdown(app);

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        if (mock)
        {
            StorageOptions storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
            app.Logger.LogWarning("Running the Mock profile: dummy Twitch credentials, fake poller/EventSub/auth, " + "a disposable recordings root at {Root}, and a per-process record store at {Database}. Never use it for production data.",
                                  storage.RecordingsRoot,
                                  storage.DatabasePath);
        }
        else if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.AllowedTwitchLogins.Count == 0)
        {
            app.Logger.LogWarning("AllowedTwitchLogins is empty; any Twitch user can administer this app. " + "Set Auth:AllowedTwitchLogins on shared networks.");
        }

        if (telemetryEnabled)
        {
            app.Services.GetRequiredService<StreamKeeprRuntimeMetrics>();

            // Here, not at construction: the meter provider listens only once the host has
            // started, and hosted services (and so the telemetry singleton) are built before that.
            app.Services.GetRequiredService<StreamKeeprTelemetry>().InitializeCounters(
                app.Services.GetRequiredService<IEventSubClient>().Transport);
        }

        string endpoint = SanitizeEndpoint(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        app.Logger.LogInformation(
            "OpenTelemetry {State}: service={Service} version={Version} environment={Environment} endpoint={Endpoint}",
            telemetryEnabled ? "enabled" : "disabled",
            StreamKeeprTelemetry.ServiceName,
            StreamKeeprTelemetry.ServiceVersion,
            TelemetryEnvironment.Name(app.Environment),
            endpoint);

        if (telemetryEnabled && endpoint is "none" or "invalid")
            app.Logger.LogWarning("OpenTelemetry is enabled without a usable OTEL_EXPORTER_OTLP_ENDPOINT; nothing will be exported.");
    });

    // Must precede authentication and antiforgery so the callback and cookies use
    // the public origin. No trusted proxy configured means no forwarded headers.
    app.UseStreamKeeprForwardedHeaders();

    if (telemetryEnabled)
    {
        app.Use(async (context, next) =>
        {
            if (TelemetryPaths.DisableHttpMetrics(context.Request.Path)
                && context.Features.Get<IHttpMetricsTagsFeature>() is { } feature)
            {
                feature.MetricsDisabled = true;
            }

            await next();
        });
    }

    // After forwarded headers (public scheme/client) and before the exception handler (its
    // 500 logs at Error) and authentication (401/403 stay visible). Everything below it,
    // media and static assets included, gets exactly one completion event.
    app.UseStreamKeeprRequestLogging();

    if (!app.Environment.IsDevelopment())
        app.UseExceptionHandler("/Error", createScopeForErrors: true);

    app.UseAuthentication();

    if (mock)
        app.UseStreamKeeprMockAuth();

    // A non-empty allowlist makes the app private (ADR-0013): sessions whose login left the list
    // end here, and the fallback policy below gates everything not marked AllowAnonymous.
    app.UseStreamKeeprPrivateMode();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.UseRecordingsMedia();

    // Public in both modes: app code and styles only, which the sign-in page needs.
    app.MapStaticAssets().AllowAnonymous();

    app.MapRazorComponents<App>()
       .AddInteractiveServerRenderMode();

    // Mock uses a fake cookie identity with stand-in login/logout endpoints on the same paths.
    if (mock)
    {
        app.MapStreamKeeprMockAuth();
    }
    else
    {
        app.MapGet(AdminAuth.LoginPath, AdminAuth.HandleLogin).AllowAnonymous();
        app.MapPost(AdminAuth.LogoutPath, AdminAuth.HandleLogoutAsync).AllowAnonymous();
    }

    app.MapMethods(LiveIndexEndpoint.Route,
                   [HttpMethods.Get, HttpMethods.Head],
                   LiveIndexEndpoint.HandleAsync);

    app.MapPut(ProgressEndpoint.Route, ProgressEndpoint.HandleAsync);
    app.MapGet(ChatEndpoint.Route, ChatEndpoint.Handle);
    app.MapGet(ViewersEndpoint.Route, ViewersEndpoint.HandleAsync);
    app.MapGet(EmoteEndpoint.Route, EmoteEndpoint.HandleAsync);
    app.MapGet(BadgeEndpoint.Route, BadgeEndpoint.HandleAsync);

    // The cookie scheme answers an API challenge with 401; the default challenge is Twitch OAuth.
    app.MapGet(ExportDownloadEndpoint.Route, ExportDownloadEndpoint.Handle)
       .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
       {
           AuthenticationSchemes = AdminAuth.CookieScheme,
       });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live"),
    }).DisableHttpMetrics().AllowAnonymous();

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
    }).DisableHttpMetrics().AllowAnonymous();

    await app.RunAsync();

    static string SanitizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "none";

        Uri? uri = ParseEndpoint(endpoint);
        return uri is null
            ? "invalid"
            : uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    static Uri? ParseEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
                ? uri
                : null;
}
// WebApplicationFactory stops the entry point after Build() with HostAbortedException;
// that is test plumbing, not a failure.
catch (Exception exception) when (exception is not HostAbortedException)
{
    // Once frozen, the bootstrap logger forwards to the configured pipeline.
    bootstrapLogger.Fatal(exception, "Stream Keepr terminated unexpectedly");

    // WebApplicationFactory reports startup failures from the entry point's exception.
    if (testOrMockHost)
        throw;

    Environment.ExitCode = 1;
}
finally
{
    if (testOrMockHost)
        bootstrapLogger.Dispose();
    else
        await Log.CloseAndFlushAsync();
}

public partial class Program;
