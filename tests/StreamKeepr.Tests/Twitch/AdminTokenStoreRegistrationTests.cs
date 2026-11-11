using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class AdminTokenStoreRegistrationTests
{
    [Fact]
    public async Task Host_registers_the_encrypted_store_and_named_auth_client()
    {
        using var root = new TempRecordingsRoot();
        using var factory = new StreamKeeprAppFactory(recordingsRoot: root.Path);
        using var client = factory.CreateClient();

        var store = factory.Services.GetRequiredService<IAdminTokenStore>();
        Assert.IsType<AdminTokenStore>(store);

        using var http = factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(TwitchHttpClients.Auth);
        Assert.Equal(new Uri("https://id.twitch.tv/"), http.BaseAddress);

        var token = new AdminToken(
            "streamer",
            "host-access",
            "host-refresh",
            TimeProvider.System.GetUtcNow().AddHours(1));
        await store.SaveAsync(token, CancellationToken.None);

        Assert.Equal("host-access", await store.GetAsync(CancellationToken.None));
        var tokenPath = AppDataPaths.AdminTokensFile(root.Path);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(tokenPath));
        Assert.DoesNotContain("host-access", text, StringComparison.Ordinal);
        Assert.DoesNotContain("host-refresh", text, StringComparison.Ordinal);
    }
}
