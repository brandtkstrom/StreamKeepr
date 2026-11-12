using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamKeepr.Components;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Mock;
using StreamKeepr.Ui;

var builder = WebApplication.CreateBuilder(args);

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
    MockEnvironment.Configure(builder);

// The host budget is the recorder job budget plus shutdown/cleanup headroom.
TimeSpan shutdownTimeout = ChannelCoordinator.JobStopBudget + TimeSpan.FromSeconds(5);
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = shutdownTimeout);

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

builder.Services.AddOptions<AuthOptions>()
    .BindConfiguration(AuthOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => options.AllowedTwitchLogins.All(AuthOptions.IsValidLogin),
        $"{AuthOptions.SectionName}:{nameof(AuthOptions.AllowedTwitchLogins)} contains an invalid Twitch login.")
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

// The session store owns session.json, index.m3u8, and the per-session locks.
builder.Services.AddSingleton<RecordingLibrary>();
builder.Services.AddSingleton<IDiskSpace, DriveInfoDiskSpace>();
builder.Services.AddSingleton<DiskGuard>();
builder.Services.AddSingleton<IStorageMonitor, StorageMonitor>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ChannelStore>();
builder.Services.AddSingleton<ProgressStore>();
builder.Services.AddSingleton<IExportLeaseRegistry, ExportLeaseRegistry>();
builder.Services.AddSingleton<IRecordingAdmin, RecordingAdmin>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<IExportService>(services => services.GetRequiredService<ExportService>());
builder.Services.AddSingleton<IProgressStore>(services => services.GetRequiredService<ProgressStore>());
builder.Services.AddSingleton<RecoveryService>();
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
    builder.Services.AddSingleton<IEventSubClient, EventSubClient>();
}

builder.Services.AddSingleton<DvrSupervisor>();
builder.Services.AddSingleton<IChannelControl>(
    services => services.GetRequiredService<DvrSupervisor>());

// Hosted services start in registration order, so preflight runs before Data
// Protection, the supervisor, and any channel or recording service.
builder.Services.AddHostedService<ToolPreflight>();

if (mock)
{
    builder.Services.AddHostedService<MockSeeder>();
    builder.Services.AddHostedService<MockLiveFeed>();
}

// Loads watch positions at startup and flushes pending ones on graceful shutdown.
builder.Services.AddHostedService(services => services.GetRequiredService<ProgressStore>());
// One FFmpeg export at a time, independent of any circuit.
builder.Services.AddHostedService(services => services.GetRequiredService<ExportService>());

// Keys live on the recordings volume and are ready before auth or token storage runs.
builder.Services.AddStreamKeeprDataProtection();

// The supervisor starts only after preflight and the key-ring check have succeeded.
builder.Services.AddHostedService(services => services.GetRequiredService<DvrSupervisor>());

builder.Services.AddHealthChecks()
    .AddCheck<DvrHealthCheck>("dvr", tags: ["live", "ready"])
    .AddCheck<DiskSpaceHealthCheck>("disk", tags: ["ready"])
    .AddCheck<LibraryLoadedHealthCheck>("library", tags: ["ready"]);

builder.Services.AddHttpClient(TwitchHttpClients.Auth, client =>
    client.BaseAddress = new Uri("https://id.twitch.tv/"));
builder.Services.AddHttpClient(TwitchHttpClients.Helix, client =>
    client.BaseAddress = new Uri("https://api.twitch.tv/"));
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
builder.Services.AddScoped<SnackbarService>();
builder.Services.AddScoped<IChannelsView, ChannelsView>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (mock)
    {
        app.Logger.LogWarning(
            "Running the Mock profile: dummy Twitch credentials, fake poller/EventSub/auth, " +
            "and a disposable recordings root at {Root}. Never use it for production data.",
            app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.RecordingsRoot);
    }
    else if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.AllowedTwitchLogins.Count == 0)
    {
        app.Logger.LogWarning(
            "AllowedTwitchLogins is empty; any Twitch user can administer this app. " +
            "Set Auth:AllowedTwitchLogins on shared networks.");
    }
});

// Must precede authentication and antiforgery so the callback and cookies use
// the public origin. No trusted proxy configured means no forwarded headers.
app.UseStreamKeeprForwardedHeaders();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseAuthentication();
if (mock)
    app.UseStreamKeeprMockAuth();
app.UseAuthorization();
app.UseAntiforgery();

// The primitive gallery exists only for local design work. Block the route
// outside Development even though the component ships in the assembly.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/dev"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
}

app.UseRecordingsMedia();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Mock uses a fake cookie identity with stand-in login/logout endpoints on the same paths.
if (mock)
{
    app.MapStreamKeeprMockAuth();
}
else
{
    app.MapGet(AdminAuth.LoginPath, AdminAuth.HandleLogin);
    app.MapPost(AdminAuth.LogoutPath, AdminAuth.HandleLogoutAsync);
}

app.MapMethods(
    LiveIndexEndpoint.Route,
    [HttpMethods.Get, HttpMethods.Head],
    LiveIndexEndpoint.HandleAsync);

app.MapPut(ProgressEndpoint.Route, ProgressEndpoint.HandleAsync);
// The cookie scheme answers an API challenge with 401; the default challenge is Twitch OAuth.
app.MapGet(ExportDownloadEndpoint.Route, ExportDownloadEndpoint.Handle)
    .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        AuthenticationSchemes = AdminAuth.CookieScheme,
    });

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});

await app.RunAsync();

public partial class Program;
