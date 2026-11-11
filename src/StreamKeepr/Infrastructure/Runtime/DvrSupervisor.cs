using System.Collections.Concurrent;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>
/// Runtime host: loads the stored channel list, recovers sessions, starts one coordinator per
/// stored channel, and feeds it from the poller. Channel control lands in a later issue.
/// </summary>
public sealed class DvrSupervisor(
    IStreamPoller poller,
    RecoveryService recovery,
    ChannelStore channelStore,
    ChannelCoordinatorFactory factory,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<DvrSupervisor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Running> _running =
        new(StringComparer.Ordinal);

    private IReadOnlyList<ChannelEntry> _channels = [];
    private IReadOnlyDictionary<string, RecoveredChannel> _recovered =
        new Dictionary<string, RecoveredChannel>(StringComparer.Ordinal);

    /// <summary>
    /// Recovery is part of host startup, not a background task: no request or coordinator may
    /// observe a partially loaded library.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await channelStore.LoadAsync(cancellationToken);
            _channels = channelStore.Snapshot;
            _recovered = await recovery.RecoverAsync(_channels, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(
                ex, "Startup failed while loading channels or recovering sessions.");
            throw;
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (ChannelEntry channel in _channels)
            StartChannel(channel, _recovered.GetValueOrDefault(channel.Login), stoppingToken);

        string[] logins = _channels.Select(channel => channel.Login).ToArray();
        Task polling = poller.RunAsync(logins, RouteAsync, stoppingToken);
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
        ChannelEntry channel, RecoveredChannel? recovered, CancellationToken stoppingToken)
    {
        bool paused = channel.Paused;
        DvrState initial = paused
            ? new DvrState.Paused(recovered?.Finished)
            : new DvrState.Idle(Finished: recovered?.Finished);
        ChannelCoordinator coordinator = factory.Create(channel.Login, initial);

        // Queue the recovery before the loop starts so it is the first event the reducer sees.
        if (!paused && recovered?.Unfinished is { } unfinished)
            _ = coordinator.PublishAsync(new DvrEvent.SessionRecovered(unfinished));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task loop = Task.Run(() => coordinator.RunAsync(cts.Token), CancellationToken.None);
        _running[channel.Login] = new Running(coordinator, cts, loop);

        _ = loop.ContinueWith(
            task =>
            {
                logger.LogCritical(
                    task.Exception,
                    "The channel loop for {Channel} crashed; stopping the host.",
                    channel.Login);
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
