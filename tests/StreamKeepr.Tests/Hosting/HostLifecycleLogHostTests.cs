using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Runtime;

namespace StreamKeepr.Tests.Hosting;

/// <summary>
/// The host lifecycle breadcrumbs through the real host: one startup summary once the host has
/// started, and one breadcrumb when shutdown begins.
/// </summary>
public sealed class HostLifecycleLogHostTests
{
    private const string StartedFragment = "Stream Keepr started";
    private const string StoppingFragment = "Stream Keepr stopping";

    [Fact]
    public async Task Startup_logs_one_information_summary_with_the_safe_fields()
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [$"{TwitchOptions.SectionName}:Channels:0"] = "alpha",
            [$"{TwitchOptions.SectionName}:Channels:1"] = "beta",
            [$"{TwitchOptions.SectionName}:EventSubTransport"] = "Conduit",
            [$"{ChatOptions.SectionName}:Enabled"] = "true",
        },
        // Watched channels make startup recovery resolve their profiles; the real poller would
        // call Twitch, so startup time would depend on the network.
        configureTestServices: services =>
            services.AddSingleton<IStreamPoller>(new ScriptedStreamPoller()));
        using HttpClient client = factory.CreateClient();

        await factory.Logs.WaitForAsync(StartedFragment);

        CapturedLogEntry summary = Assert.Single(StartupSummaries(factory));
        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Equal(StreamKeeprLogging.TestingEnvironmentName, summary.Properties["Environment"]);
        Assert.Equal(2, summary.Properties["ChannelCount"]);
        Assert.Equal(factory.RecordingsRoot, summary.Properties["Root"]);
        Assert.Equal("Conduit", summary.Properties["Transport"]?.ToString());
        Assert.Equal(true, summary.Properties["ChatCaptureEnabled"]);
    }

    [Fact]
    public async Task A_stop_request_logs_one_information_stopping_breadcrumb()
    {
        using var factory = new StreamKeeprAppFactory();
        using HttpClient client = factory.CreateClient();
        await factory.Logs.WaitForAsync(StartedFragment);
        Assert.DoesNotContain(factory.Logs.Messages,
                              message => message.StartsWith(StoppingFragment, StringComparison.Ordinal));

        // SIGTERM and `docker compose stop` reach the host as this same stop request.
        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await factory.Logs.WaitForAsync(StoppingFragment);

        CapturedLogEntry stopping = Assert.Single(factory.Logs.Entries, entry =>
            entry.Message.StartsWith(StoppingFragment, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, stopping.Level);
    }

    [Fact]
    public async Task The_breadcrumbs_never_carry_the_client_secret_or_chat_token()
    {
        const string clientSecret = "lifecycle-client-secret-7f3a";
        const string chatToken = "oauth:lifecycle-chat-token-91c2";
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            [$"{TwitchOptions.SectionName}:ClientSecret"] = clientSecret,
            [$"{ChatOptions.SectionName}:Enabled"] = "true",
            [$"{ChatOptions.SectionName}:TwitchUserOAuthToken"] = chatToken,
            [$"{ChatOptions.SectionName}:TwitchLogin"] = "keeprbot",
        });
        using HttpClient client = factory.CreateClient();
        await factory.Logs.WaitForAsync(StartedFragment);
        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await factory.Logs.WaitForAsync(StoppingFragment);

        // Every captured entry, not only the breadcrumbs, rendered and structured.
        foreach (CapturedLogEntry entry in factory.Logs.Entries)
        {
            string values = string.Join('|', entry.Properties.Values);
            foreach (string secret in new[] { clientSecret, chatToken })
            {
                Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, values, StringComparison.Ordinal);
            }
        }

        // The chat login is personal data; the breadcrumbs carry only the chat on/off flag.
        foreach (CapturedLogEntry breadcrumb in factory.Logs.Entries.Where(entry =>
                     entry.Message.StartsWith(StartedFragment, StringComparison.Ordinal)
                     || entry.Message.StartsWith(StoppingFragment, StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("keeprbot", breadcrumb.Message, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<CapturedLogEntry> StartupSummaries(StreamKeeprAppFactory factory) =>
        factory.Logs.Entries.Where(entry =>
            entry.Message.StartsWith(StartedFragment, StringComparison.Ordinal));
}
