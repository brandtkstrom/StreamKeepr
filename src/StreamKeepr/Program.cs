using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamKeepr.Components;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Ui;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddStreamKeeprForwardedHeaders();

builder.Services.AddSingleton(TimeProvider.System);

// The session store owns session.json, index.m3u8, and the per-session locks.
builder.Services.AddSingleton<RecordingLibrary>();
builder.Services.AddSingleton<IDiskSpace, DriveInfoDiskSpace>();
builder.Services.AddSingleton<DiskGuard>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ChannelStore>();
builder.Services.AddSingleton<RecoveryService>();
builder.Services.AddSingleton<RecorderJob>();
builder.Services.AddSingleton<IRecorder>(services => services.GetRequiredService<RecorderJob>());

// Runtime: one coordinator per stored channel, fed by the Helix poller.
builder.Services.AddSingleton<IRetryJitter, RandomizedRetryJitter>();
builder.Services.AddSingleton<DvrStatus>();
builder.Services.AddSingleton<ChannelCoordinatorFactory>();
builder.Services.AddSingleton<TwitchTokenProvider>();
builder.Services.AddSingleton<PollSchedule>();
builder.Services.AddSingleton<IStreamPoller, TwitchStreamPoller>();
builder.Services.AddSingleton<IEventSubSocketFactory, ClientWebSocketEventSubSocketFactory>();
builder.Services.AddSingleton<IEventSubClient, EventSubClient>();
builder.Services.AddSingleton<DvrSupervisor>();
builder.Services.AddSingleton<IChannelControl>(
    services => services.GetRequiredService<DvrSupervisor>());

// Hosted services start in registration order, so preflight runs before Data
// Protection, the supervisor, and any channel or recording service.
builder.Services.AddHostedService<ToolPreflight>();

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

builder.Services.AddStreamKeeprAdminAuth();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
builder.Services.AddScoped<UiPreferences>();
builder.Services.AddScoped<ViewerClock>();
builder.Services.AddScoped<SnackbarService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.AllowedTwitchLogins.Count == 0)
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

app.MapGet(AdminAuth.LoginPath, AdminAuth.HandleLogin);
app.MapPost(AdminAuth.LogoutPath, AdminAuth.HandleLogoutAsync);
app.MapMethods(
    LiveIndexEndpoint.Route,
    [HttpMethods.Get, HttpMethods.Head],
    LiveIndexEndpoint.HandleAsync);

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
