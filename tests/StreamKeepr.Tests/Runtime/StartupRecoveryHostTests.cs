using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Runtime;

public sealed class StartupRecoveryHostTests
{
    [Fact]
    public async Task Startup_recovery_loads_the_library_and_queues_SessionRecovered()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.WritePart(1);

        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                [$"{TwitchOptions.SectionName}:Channels:0"] = "streamer",
            },
            recordingsRoot: context.Root);
        using var client = factory.CreateClient();

        DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            status,
            "streamer",
            current => current.State is DvrState.Idle { Resume: not null });

        var idle = Assert.IsType<DvrState.Idle>(snapshot.State);
        Assert.Equal(context.Session.Id, idle.Resume!.Session.Id);
        Assert.Equal(2, idle.Resume.NextPart);
        Assert.True(factory.Services.GetRequiredService<RecordingLibrary>().IsLoaded);
    }
}
