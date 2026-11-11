using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>
/// Minimal runtime host for the tracer: one coordinator per configured channel, fed by the
/// poller. Channel storage, recovery, and channel control land in later issues.
/// </summary>
public sealed class DvrSupervisor(
    IOptions<TwitchOptions> options,
    IStreamPoller poller,
    ChannelCoordinatorFactory factory,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<DvrSupervisor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Running> _running =
        new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (string channel in options.Value.Channels)
            StartChannel(channel, stoppingToken);

        Task polling = poller.RunAsync(options.Value.Channels, RouteAsync, stoppingToken);
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

    private void StartChannel(string channel, CancellationToken stoppingToken)
    {
        ChannelCoordinator coordinator = factory.Create(channel, new DvrState.Idle());
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
