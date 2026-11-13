using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Mock;

/// <summary>
/// Fake EventSub client: no WebSocket and no subscriptions. It waits for test-controlled pushes
/// from <see cref="MockTwitch"/> and routes them like a real stream.online/offline notification,
/// starting a short burst window. Polling remains the authoritative liveness source.
/// </summary>
public sealed class MockEventSubClient(
    MockTwitch twitch,
    PollSchedule schedule,
    ChannelStore channels,
    ILogger<MockEventSubClient> logger) : IEventSubClient
{
    private static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(10);

    public string Transport => "Mock";

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
        // The Mock profile is a working fake EventSub: push arrives through MockTwitch.
        // No watched channels means there is nothing to cover, not that push is on.
        channels.Changed += UpdatePushStatus;
        UpdatePushStatus();

        try
        {
            await foreach (MockPush push in twitch.ReadPushesAsync(ct))
            {
                logger.LogInformation(
                    "Routing a mock EventSub push for {Channel}.", push.Login);
                await route(push.Login, push.Observation, ct);
                schedule.Burst(BurstDuration);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            channels.Changed -= UpdatePushStatus;
        }
    }

    // Every watched channel counts as covered: mock pushes need no subscriptions.
    private void UpdatePushStatus()
    {
        IReadOnlyList<ChannelEntry> watched = channels.Snapshot;
        schedule.SetPushStatus(PushStatus.FromCoverage(
            watched.Select(channel => channel.Login), watched.Count));
    }
}
