using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// Keeps the chat source joined to currently recording channels and routes its events to the
/// per-session chat log. Chat failures are logged; they never affect recording.
/// </summary>
public sealed class ChatCaptureService(
    IChatSource source,
    RecordingLibrary library,
    ChatLogStore store,
    IOptions<ChatOptions> options,
    TimeProvider clock,
    ILogger<ChatCaptureService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);

    // Reconcile owns _active; the consumer and Close share the concurrent maps.
    private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastMessageAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _outageFrom = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        // Either loop ending stops the other, so a dead source cannot leave joins running
        // with nothing consuming their events.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            Task consume = RunUntilStoppedAsync(ConsumeAsync, stop);
            Task reconcile = RunUntilStoppedAsync(ReconcileAsync, stop);
            await Task.WhenAll(consume, reconcile);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            // A chat failure must never stop the recorder host.
            logger.LogError(ex, "The chat capture service stopped unexpectedly; chat is not captured until restart.");
        }
        finally
        {
            store.CloseAll(finalize: false);
        }
    }

    private static async Task RunUntilStoppedAsync(
        Func<CancellationToken, Task> loop, CancellationTokenSource stop)
    {
        try
        {
            await loop(stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (ChatSourceEvent evt in source.ReadAllAsync(ct))
        {
            try
            {
                Route(evt);
            }
            catch (Exception ex)
            {
                // One bad write must not stop capture for every channel.
                logger.LogWarning(
                    ex, "A chat event for {Channel} could not be persisted.", evt.Channel);
            }
        }
    }

    private void Route(ChatSourceEvent evt)
    {
        switch (evt)
        {
            case ChatSourceEvent.Disconnected disconnected:
                // Twitch does not replay missed chat: the outage runs from the last captured
                // message (or the disconnect, when none arrived) to the rejoin.
                _outageFrom.TryAdd(
                    disconnected.Channel,
                    _lastMessageAt.GetValueOrDefault(disconnected.Channel, disconnected.AtMs));
                return;

            case ChatSourceEvent.Joined joined:
                EndOutage(joined.Channel, joined.AtMs);
                return;

            case ChatSourceEvent.MessageEvent message:
                _lastMessageAt[message.Channel] = message.AtMs;
                break;
        }

        store.Append(evt.Channel, evt);
    }

    private void EndOutage(string channel, long toMs)
    {
        if (!_outageFrom.TryRemove(channel, out long fromMs) || toMs <= fromMs)
            return;

        store.Append(channel, new ChatSourceEvent.Gap(
            channel, clock.GetUtcNow().ToUnixTimeMilliseconds(), fromMs, toMs, "irc-disconnected"));
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ReconcileInterval, clock);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await ReconcileOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat session reconciliation failed.");
            }
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        if (!library.IsLoaded)
            return;

        var recording = library.All
            .Where(summary => summary.IsRecording)
            .GroupBy(summary => summary.Channel, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(summary => summary.StartedAt).First(),
                StringComparer.Ordinal);

        foreach ((string channel, RecordingSummary summary) in recording)
        {
            if (_active.TryGetValue(channel, out string? currentId))
            {
                if (string.Equals(currentId, summary.Id, StringComparison.Ordinal))
                    continue;

                await CloseAsync(channel, currentId, ct);
            }

            try
            {
                store.OpenSession(summary, source.Source);
                await source.JoinAsync(channel, ct);
                _active[channel] = summary.Id;
                logger.LogInformation("Chat capture joined {Channel}.", channel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not marked active, so the next tick retries the join.
                store.CloseSession(summary.Id, finalize: false);
                logger.LogWarning(ex, "Chat capture could not join {Channel}; retrying.", channel);
            }
        }

        foreach ((string channel, string sessionId) in _active.ToArray())
        {
            if (!recording.ContainsKey(channel))
                await CloseAsync(channel, sessionId, ct);
        }
    }

    private async Task CloseAsync(string channel, string sessionId, CancellationToken ct)
    {
        _active.Remove(channel);
        EndOutage(channel, clock.GetUtcNow().ToUnixTimeMilliseconds());
        _lastMessageAt.TryRemove(channel, out _);
        try
        {
            await source.PartAsync(channel, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chat capture could not leave {Channel}.", channel);
        }

        store.CloseSession(sessionId, finalize: true);
        logger.LogInformation("Chat capture left {Channel}.", channel);
    }
}
