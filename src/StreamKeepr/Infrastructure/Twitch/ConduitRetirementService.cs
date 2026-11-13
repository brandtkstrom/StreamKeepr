using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Rollback cleanup (ADR-0007): when the transport flag is <see cref="EventSubTransportMode.WebSocket"/>,
/// deletes the conduit an earlier Conduit run left behind so its subscriptions stop. Does nothing
/// in Conduit mode or when no conduit was saved.
/// </summary>
public sealed class ConduitRetirementService(
    IOptions<TwitchOptions> options,
    IConduitStateStore stateStore,
    EventSubConduitApi api,
    ILogger<ConduitRetirementService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RetireAsync(stoppingToken);

    public async Task RetireAsync(CancellationToken ct)
    {
        TwitchOptions twitch = options.Value;
        if (twitch.EventSubTransport != EventSubTransportMode.WebSocket)
            return;

        try
        {
            ConduitState? saved = await stateStore.LoadAsync(ct);
            if (saved is null)
                return;

            if (saved.ClientId != twitch.ClientId)
            {
                logger.LogWarning(
                    "Forgetting EventSub conduit {ConduitId}: it belongs to a different Twitch client ID, "
                    + "so it cannot be deleted. Twitch deletes it 72 hours after its shard disconnected.",
                    saved.ConduitId);
                await stateStore.ClearAsync(ct);
                return;
            }

            await api.DeleteConduitAsync(saved.ConduitId, ct);
            await stateStore.ClearAsync(ct);
            logger.LogInformation(
                "Deleted EventSub conduit {ConduitId} because EventSub now uses the WebSocket transport.",
                saved.ConduitId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Polling and the WebSocket transport do not depend on this; keep the state file so
            // the next start retries. An unreachable conduit expires on Twitch's side.
            logger.LogWarning(
                ex,
                "Could not delete the leftover EventSub conduit; the next start retries. Twitch deletes "
                + "it 72 hours after its shard disconnected.");
        }
    }
}
