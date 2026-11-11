using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Mock;

/// <summary>
/// Deterministic stand-in for the Helix poller. It ticks every two seconds, routes the current
/// <see cref="MockTwitch"/> observation for each watched channel, and records the poll so the
/// DVR health check stays ready. No network or credentials are involved.
/// </summary>
public sealed class MockStreamPoller(
    MockTwitch twitch,
    PollSchedule schedule,
    ILogger<MockStreamPoller> logger) : IStreamPoller
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task RunAsync(
        Func<IReadOnlyList<string>> logins,
        StreamObservationRoute route,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (string login in logins()
                             .Select(TwitchLogin.Normalize)
                             .Where(TwitchLogin.IsValid)
                             .Distinct(StringComparer.Ordinal))
                {
                    await route(login, twitch.Observe(login), ct);
                }

                schedule.Polled();
                await schedule.WaitAsync(PollInterval, PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct) =>
        Task.FromResult(twitch.FindProfile(login));

    public Task<ChannelInfo?> GetChannelInfoAsync(string broadcasterId, CancellationToken ct) =>
        Task.FromResult(twitch.FindChannelInfo(broadcasterId));

    public void Nudge()
    {
        logger.LogDebug("Mock poller nudged.");
        schedule.Nudge();
    }
}
