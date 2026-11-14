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
    private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastMessageAt =
        new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        try
        {
            await Task.WhenAll(
                this.ConsumeAsync(stoppingToken),
                this.ReconcileAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            // A chat failure must never stop the recorder host.
            logger.LogError(ex, "The chat capture service stopped unexpectedly.");
        }
        finally
        {
            store.CloseAll(finalize: false);
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (ChatSourceEvent evt in source.ReadAllAsync(ct))
        {
            try
            {
                if (evt is ChatSourceEvent.MessageEvent message)
                    this._lastMessageAt[message.Message.Channel] = message.Message.AtMs;

                if (evt is ChatSourceEvent.Reconnect reconnect)
                {
                    if (this._lastMessageAt.TryGetValue(reconnect.Channel, out long from) &&
                        reconnect.AtMs > from)
                    {
                        store.Append(
                            reconnect.Channel,
                            new ChatSourceEvent.Gap(
                                reconnect.Channel,
                                clock.GetUtcNow().ToUnixTimeMilliseconds(),
                                from,
                                reconnect.AtMs,
                                "irc-disconnected"));
                    }

                    continue;
                }

                store.Append(evt.Channel, evt);
            }
            catch (Exception ex)
            {
                // One bad write must not stop capture for every channel.
                logger.LogWarning(
                    ex, "A chat event for {Channel} could not be persisted.", evt.Channel);
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                this.Reconcile();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat session reconciliation failed.");
            }
        }
    }

    private void Reconcile()
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
            if (this._active.TryGetValue(channel, out string? currentId))
            {
                if (string.Equals(currentId, summary.Id, StringComparison.Ordinal))
                    continue;

                this.Close(channel, currentId);
            }

            try
            {
                store.OpenSession(summary, source.Source);
                this._active[channel] = summary.Id;
                _ = source.JoinAsync(channel, CancellationToken.None);
                logger.LogInformation("Chat capture joined {Channel}.", channel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat capture could not open {Channel}.", channel);
            }
        }

        foreach ((string channel, string sessionId) in this._active.ToArray())
        {
            if (recording.ContainsKey(channel))
                continue;

            this.Close(channel, sessionId);
        }
    }

    private void Close(string channel, string sessionId)
    {
        this._active.Remove(channel);
        this._lastMessageAt.TryRemove(channel, out _);
        _ = source.PartAsync(channel, CancellationToken.None);
        store.CloseSession(sessionId, finalize: true);
        logger.LogInformation("Chat capture left {Channel}.", channel);
    }
}
