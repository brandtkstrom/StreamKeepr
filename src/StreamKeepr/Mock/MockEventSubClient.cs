using StreamKeepr.Domain;
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
    ILogger<MockEventSubClient> logger) : IEventSubClient
{
    private static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(10);

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
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
    }
}
