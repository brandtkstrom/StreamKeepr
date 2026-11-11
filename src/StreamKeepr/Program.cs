using Microsoft.Extensions.Options;
using StreamKeepr.Components;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Ui;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<TwitchOptions>()
    .BindConfiguration(TwitchOptions.SectionName)
    .ValidateDataAnnotations()
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

// Hosted services start in registration order, so preflight runs before Data
// Protection, the supervisor, and any channel or recording service.
builder.Services.AddHostedService<ToolPreflight>();

// Keys live on the recordings volume and are ready before auth or token storage runs.
builder.Services.AddStreamKeeprDataProtection();

builder.Services.AddHttpClient(TwitchHttpClients.Auth, client =>
    client.BaseAddress = new Uri("https://id.twitch.tv/"));
builder.Services.AddSingleton<IAdminTokenStore, AdminTokenStore>();

builder.Services.AddStreamKeeprAdminAuth();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
builder.Services.AddScoped<UiPreferences>();
builder.Services.AddScoped<ViewerClock>();

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

app.UseRecordingsMedia();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet(AdminAuth.LoginPath, AdminAuth.HandleLogin);
app.MapPost(AdminAuth.LogoutPath, AdminAuth.HandleLogoutAsync);

await app.RunAsync();

public partial class Program;
