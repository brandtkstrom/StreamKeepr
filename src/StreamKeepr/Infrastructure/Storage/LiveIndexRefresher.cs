using Microsoft.Extensions.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Refreshes the <see cref="LiveIndex"/> immediately and then every
/// <c>LivePlaylist:RefreshSeconds</c>, on the injected clock. A session that is still busy from
/// the last tick skips this one.
/// </summary>
public sealed class LiveIndexRefresher(LiveIndex index, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(index.RefreshInterval, clock);
        try
        {
            // StartAsync must return promptly; the immediate refresh runs on this loop.
            await Task.Yield();
            await index.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await index.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
