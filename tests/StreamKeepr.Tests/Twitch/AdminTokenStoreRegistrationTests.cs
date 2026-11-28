using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class AdminTokenStoreRegistrationTests
{
    [Fact]
    public void Host_registers_the_encrypted_store_and_named_auth_client()
    {
        using var root = new TempRecordingsRoot();
        using var factory = new StreamKeeprAppFactory(recordingsRoot: root.Path);
        using var client = factory.CreateClient();

        var store = factory.Services.GetRequiredService<IAdminTokenStore>();
        Assert.IsType<AdminTokenStore>(store);

        using var http = factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(TwitchHttpClients.Auth);
        Assert.Equal(new Uri("https://id.twitch.tv/"), http.BaseAddress);
    }

    [Fact]
    public async Task A_token_saved_at_sign_in_is_one_ciphertext_row_that_a_restarted_host_reads()
    {
        using var root = new TempRecordingsRoot();
        var token = new AdminToken(
            "streamer",
            "host-access",
            "host-refresh",
            TimeProvider.System.GetUtcNow().AddHours(1));

        using (var first = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = first.CreateClient();
            await first.Services.GetRequiredService<IAdminTokenStore>().SaveAsync(token, CancellationToken.None);

            var records = first.Services.GetRequiredService<IDbContextFactory<RecordStoreDbContext>>();
            await using RecordStoreDbContext db = await records.CreateDbContextAsync();
            AdminTokenRow row = Assert.Single(await db.AdminTokens.AsNoTracking().ToListAsync());
            Assert.DoesNotContain("host-access", row.Ciphertext, StringComparison.Ordinal);
            Assert.DoesNotContain("host-refresh", row.Ciphertext, StringComparison.Ordinal);
        }

        // The restarted host reads the row with the key ring it kept beside the database, which
        // is what EventSub and the admin session validator ask for.
        using (var second = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = second.CreateClient();
            var store = second.Services.GetRequiredService<IAdminTokenStore>();

            Assert.Equal("host-access", await store.GetAsync(CancellationToken.None));
            Assert.DoesNotContain(
                second.Logs.Messages,
                message => message.Contains("host-access", StringComparison.Ordinal)
                    || message.Contains("host-refresh", StringComparison.Ordinal));
        }

        string appData = AppDataPaths.AppDataDirectory(root.Path);
        Assert.False(File.Exists(Path.Combine(appData, AppDataPaths.AdminTokensFileName)));
        Assert.False(File.Exists(Path.Combine(appData, AppDataPaths.EventSubConduitFileName)));
    }
}
