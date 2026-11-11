using Microsoft.Extensions.Options;
using StreamKeepr.Components;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

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

builder.Services.AddSingleton(TimeProvider.System);

// Keys live on the recordings volume and are ready before auth or token storage runs.
builder.Services.AddStreamKeeprDataProtection();

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

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

public partial class Program;
