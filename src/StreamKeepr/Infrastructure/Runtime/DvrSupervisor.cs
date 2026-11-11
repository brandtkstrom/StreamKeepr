using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>
/// Runtime host: recovers sessions, starts one coordinator per configured channel, and feeds
/// it from the poller. Channel storage and channel control land in later issues.
/// </summary>
public sealed class DvrSupervisor(
    IOptions<TwitchOptions> options,
    IStreamPoller poller,
    RecoveryService recovery,
    ChannelCoordinatorFactory factory,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<DvrSupervisor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Running> _running =
        new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<string> channels = options.Value.Channels;
        IReadOnlyDictionary<string, RecoveredChannel> recovered;

        try
        {
            // No coordinator may process events until every session has been classified.
            recovered = await recovery.RecoverAsync(channels, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Startup recovery failed; stopping the host.");
            throw;
        }

        foreach (string channel in channels)
            StartChannel(channel, recovered.GetValueOrDefault(channel), stoppingToken);

        Task polling = poller.RunAsync(channels, RouteAsync, stoppingToken);
        _ = polling.ContinueWith(
            task =>
            {
                logger.LogCritical(task.Exception, "The stream poller crashed; stopping the host.");
                lifetime.StopApplication();
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Stop channels in parallel; each coordinator finalizes its active part on the way out.
            foreach (Running running in _running.Values)
                await running.Cts.CancelAsync();

            await Task.WhenAll(_running.Values.Select(running => running.Loop))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await polling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            foreach (Running running in _running.Values)
                running.Cts.Dispose();
        }
    }

    private void StartChannel(
        string channel, RecoveredChannel? recovered, CancellationToken stoppingToken)
    {
        ChannelCoordinator coordinator = factory.Create(
            channel, new DvrState.Idle(Finished: recovered?.Finished));

        // Queue the recovery before the loop starts so it is the first event the reducer sees.
        if (recovered?.Unfinished is { } unfinished)
            _ = coordinator.PublishAsync(new DvrEvent.SessionRecovered(unfinished));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task loop = Task.Run(() => coordinator.RunAsync(cts.Token), CancellationToken.None);
        _running[channel] = new Running(coordinator, cts, loop);

        _ = loop.ContinueWith(
            task =>
            {
                logger.LogCritical(
                    task.Exception,
                    "The channel loop for {Channel} crashed; stopping the host.",
                    channel);
                lifetime.StopApplication();
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private ValueTask RouteAsync(
        string login, StreamObservation observation, CancellationToken ct) =>
        _running.TryGetValue(login, out Running? running)
            ? running.Coordinator.PublishAsync(
                new DvrEvent.StreamStatusObserved(observation, clock.GetUtcNow()), ct)
            : ValueTask.CompletedTask;

    private sealed record Running(
        ChannelCoordinator Coordinator, CancellationTokenSource Cts, Task Loop);
}
