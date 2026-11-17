using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Runtime;

namespace StreamKeepr.Tests.Hosting;

public sealed class WebAppSmokeTests
{
    [Fact]
    public async Task Home_page_returns_200()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Stream Keepr", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Host_registers_the_time_provider_and_binds_the_options()
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [$"{AuthOptions.SectionName}:AllowedTwitchLogins:0"] = "Allowed_Login",
        });

        Assert.Same(TimeProvider.System, factory.Services.GetRequiredService<TimeProvider>());

        var twitch = factory.Services.GetRequiredService<IOptions<TwitchOptions>>().Value;
        Assert.Equal("test-client-id", twitch.ClientId);
        Assert.Equal("test-client-secret", twitch.ClientSecret);

        var recorder = factory.Services.GetRequiredService<IOptions<RecorderOptions>>().Value;
        Assert.Equal("best", recorder.Quality);
        Assert.Equal(6, recorder.SegmentSeconds);
        Assert.Equal(10, recorder.FinalizeTimeoutSeconds);

        var storage = factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        Assert.Equal(factory.RecordingsRoot, storage.RecordingsRoot);

        var auth = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        Assert.Equal(["allowed_login"], auth.AllowedTwitchLogins);

        var chat = factory.Services.GetRequiredService<IOptions<ChatOptions>>().Value;
        Assert.False(chat.Emotes.Bttv.Enabled);
    }

    [Fact]
    public void Host_shutdown_timeout_is_40_seconds()
    {
        using var factory = new StreamKeeprAppFactory();

        HostOptions options = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.True(options.ShutdownTimeout > ChannelCoordinator.JobStopBudget);
        Assert.Equal(
            ChannelCoordinator.JobStopBudget + TimeSpan.FromSeconds(5),
            options.ShutdownTimeout);
    }

    [Fact]
    public async Task Empty_allowlist_logs_the_open_admin_warning()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        // The warning is logged from an ApplicationStarted callback, which can run after
        // CreateClient returns. Waiting for ApplicationStarted ourselves is not enough:
        // token callbacks run in reverse registration order, so ours fires before the app's.
        // Wait for the message itself instead.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!HasWarning() && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(HasWarning(), "the open-admin warning was not logged");

        bool HasWarning() => factory.Logs.Messages.Any(
            message => message.Contains("AllowedTwitchLogins is empty", StringComparison.Ordinal));
    }
}
