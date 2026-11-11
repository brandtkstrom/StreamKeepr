using Microsoft.Extensions.Logging;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Temporary production binding until the Helix poller lands (#40). It keeps the host healthy
/// without pretending to observe channels. Remove this registration when #40 registers the real
/// <see cref="IStreamPoller"/>.
/// </summary>
public sealed class UnconfiguredStreamPoller(ILogger<UnconfiguredStreamPoller> logger) : IStreamPoller
{
    private int _warned;

    public async Task RunAsync(
        Func<IReadOnlyList<string>> logins,
        StreamObservationRoute route,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logins);
        ArgumentNullException.ThrowIfNull(route);

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "No Twitch stream poller is configured; live streams will not be detected. " +
                "The Helix poller lands with #40.");
        }

        await Task.Delay(Timeout.Infinite, ct);
    }

    public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct) =>
        Task.FromResult<ChannelProfile?>(null);

    public void Nudge()
    {
    }
}
