using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Runtime;

public sealed class DvrSupervisorTests
{
    private const string Channel = "streamer";

    [Fact]
    public async Task A_faulted_channel_loop_stops_the_host()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var clock = new FakeTimeProvider(
                new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
            IOptions<StorageOptions> storageOptions = Options.Create(
                new StorageOptions { RecordingsRoot = root, MinFreeGb = 1 });
            var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
            var store = new SessionStore(
                Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
                library,
                clock,
                NullLogger<SessionStore>.Instance);
            var disk = new DiskGuard(
                storageOptions,
                new FakeDiskSpace(DiskGuard.BytesPerGib * 2, DiskGuard.BytesPerGib * 4));
            var status = new DvrStatus();
            var recorder = new FakeRecorder
            {
                OnRun = async (call, _) =>
                    await call.PublishAsync(new DvrEvent.RecorderExited(
                        call.Job, clock.GetUtcNow(), RecorderExitKind.Exited, "no media")),
            };
            var factory = new ChannelCoordinatorFactory(
                Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
                storageOptions,
                recorder,
                store,
                disk,
                status,
                new ThrowingRetryJitter(),
                clock,
                NullLoggerFactory.Instance);
            var channelStore = new ChannelStore(
                Options.Create(new TwitchOptions { Channels = [Channel] }),
                storageOptions,
                NullLogger<ChannelStore>.Instance);
            var recovery = new RecoveryService(
                store, library, storageOptions, NullLogger<RecoveryService>.Instance);
            var poller = new ScriptedStreamPoller();
            var lifetime = new FakeHostApplicationLifetime();
            var logs = new ListLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
            using var supervisor = new DvrSupervisor(
                poller,
                new NoopEventSubClient(),
                recovery,
                channelStore,
                status,
                factory,
                lifetime,
                clock,
                loggerFactory.CreateLogger<DvrSupervisor>());

            await supervisor.StartAsync(CancellationToken.None);
            await poller.Started.WaitAsync(TimeSpan.FromSeconds(5));

            poller.Send(Channel, new StreamObservation.Live(
                new LiveStream(
                    "123456789",
                    "A stream",
                    "Just Chatting",
                    new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero),
                    ThumbnailUrl: null)));

            await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(logs.Messages,
                message => message.Contains("crashed", StringComparison.Ordinal));

            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoopEventSubClient : IEventSubClient
    {
        public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }
}
