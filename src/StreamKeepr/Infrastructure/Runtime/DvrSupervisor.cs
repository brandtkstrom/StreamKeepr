using System.Collections.Concurrent;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>
/// Runtime host: loads the stored channel list, recovers sessions, starts one coordinator per
/// stored channel, feeds it from the poller, and serializes channel-control operations.
/// </summary>
public sealed class DvrSupervisor(
    IStreamPoller poller,
    RecoveryService recovery,
    ChannelStore channelStore,
    DvrStatus status,
    ChannelCoordinatorFactory factory,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<DvrSupervisor> logger) : BackgroundService, IChannelControl
{
    private readonly ConcurrentDictionary<string, Running> _running =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _controlGate = new(1, 1);

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

        Task polling = poller.RunAsync(
            () => channelStore.Snapshot.Select(channel => channel.Login).ToArray(),
            RouteAsync,
            stoppingToken);
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

    public async Task AddChannelAsync(string login, CancellationToken ct)
    {
        login = NormalizeLogin(login);

        await _controlGate.WaitAsync(ct);
        try
        {
            if (channelStore.Snapshot.Any(channel => channel.Login == login))
                throw new InvalidOperationException($"Channel '{login}' is already watched.");

            ChannelProfile? profile = await poller.GetUserAsync(login, ct);
            if (profile is null)
                throw new UnknownChannelException(login);

            ChannelEntry entry = await channelStore.AddAsync(
                new ChannelEntry(
                    login,
                    UserId: profile.UserId,
                    DisplayName: profile.DisplayName,
                    AvatarUrl: profile.AvatarUrl),
                ct);

            if (!_running.ContainsKey(login))
                StartChannel(entry, recovered: null, lifetime.ApplicationStopping);

            poller.Nudge();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task RemoveChannelAsync(string login, CancellationToken ct)
    {
        login = NormalizeLogin(login);

        await _controlGate.WaitAsync(ct);
        try
        {
            GetWatched(login);
            if (_running.TryRemove(login, out Running? running))
            {
                await running.Coordinator.PublishAsync(
                    new DvrEvent.StopRequested(), CancellationToken.None);
                running.Coordinator.Complete();
                await running.Loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                running.Cts.Dispose();
            }

            await channelStore.RemoveAsync(login, ct);
            status.Remove(login);
            poller.Nudge();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task StopRecordingAsync(string login, CancellationToken ct)
    {
        login = NormalizeLogin(login);

        await _controlGate.WaitAsync(ct);
        try
        {
            GetWatched(login);
            await channelStore.SetPausedAsync(login, paused: true, ct);
            if (_running.TryGetValue(login, out Running? running))
            {
                await running.Coordinator.PublishAsync(
                    new DvrEvent.StopRequested(), CancellationToken.None);
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task ResumeRecordingAsync(string login, CancellationToken ct)
    {
        login = NormalizeLogin(login);

        await _controlGate.WaitAsync(ct);
        try
        {
            GetWatched(login);
            await channelStore.SetPausedAsync(login, paused: false, ct);
            if (_running.TryGetValue(login, out Running? running))
            {
                await running.Coordinator.PublishAsync(
                    new DvrEvent.ResumeRequested(), CancellationToken.None);
            }

            poller.Nudge();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private ChannelEntry GetWatched(string login) =>
        channelStore.Snapshot.FirstOrDefault(entry => entry.Login == login)
        ?? throw new KeyNotFoundException($"Channel '{login}' is not watched.");

    private static string NormalizeLogin(string login)
    {
        login = TwitchLogin.Normalize(login);
        if (!TwitchLogin.IsValid(login))
            throw new ArgumentException($"'{login}' is not a valid Twitch login.", nameof(login));

        return login;
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
