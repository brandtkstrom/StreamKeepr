using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

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
    }

    [Fact]
    public void Empty_allowlist_logs_the_open_admin_warning()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        Assert.Contains(factory.Logs.Messages,
            message => message.Contains("AllowedTwitchLogins is empty", StringComparison.Ordinal));
    }
}
