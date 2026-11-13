using System.Globalization;
using System.Text.Json;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// EventSub client core. It runs the WebSocket session loop (keepalive, reconnect handoff,
/// backoff), routes notifications to the channel coordinator, and recovers from disconnects.
/// The <see cref="IEventSubBinding"/> decides how a session authenticates and subscribes:
/// a user-token WebSocket or a conduit shard.
/// </summary>
public sealed class EventSubClient(
    IEventSubSocketFactory socketFactory,
    IEventSubBinding binding,
    ChannelStore channels,
    IStreamPoller poller,
    PollSchedule schedule,
    IRetryJitter jitter,
    TimeProvider clock,
    ILogger<EventSubClient> logger) : IEventSubClient
{
    private static readonly Uri BaseUri = new("wss://eventsub.wss.twitch.tv/ws");
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeepaliveGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultKeepalive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BurstDuration = TimeSpan.FromMinutes(3);

    // Twitch closes the old connection (4004) 30 s after session_reconnect.
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(30);

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
        TimeSpan backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            var state = new SessionState();
            IEventSubSessionBinding? session;
            try
            {
                session = await binding.BeginSessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EventSub could not start a session; polling continues.");
                schedule.SetPushStatus(PushStatus.Reconnecting);
                session = null;
                state.SetupFailed = true;
            }

            if (session is not null)
            {
                // Setup succeeded, but no subscription delivers until the session welcomes us.
                schedule.SetPushStatus(PushStatus.Reconnecting);

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                void Restart() => sessionCts.Cancel();

                channels.SubscriptionsChanged += Restart;
                binding.RestartRequested += Restart;

                try
                {
                    await RunSessionAsync(session, route, BaseUri, state, sessionCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
                {
                    // Channel/binding change: reconnect and recompute subscriptions.
                }
                catch (EventSubRestartException ex)
                {
                    logger.LogWarning("EventSub restart: {Reason}", ex.Message);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "EventSub session failed.");
                }
                finally
                {
                    channels.SubscriptionsChanged -= Restart;
                    binding.RestartRequested -= Restart;
                    schedule.SetPushStatus(PushStatus.Reconnecting);
                }
            }
            else if (!state.SetupFailed)
            {
                // The binding already waited (for example for an admin token).
                continue;
            }

            if (state.HadSubscriptions)
                backoff = InitialBackoff;

            if (ct.IsCancellationRequested)
                break;

            TimeSpan delay = jitter.Apply(backoff);
            backoff = TimeSpan.FromTicks(
                Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));

            try
            {
                await Task.Delay(delay, clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(
        IEventSubSessionBinding session,
        StreamObservationRoute route,
        Uri uri,
        SessionState state,
        CancellationToken ct)
    {
        IEventSubSocket socket = socketFactory.Create();
        ReconnectHandoff? handoff = null;
        bool socketOpen = true;
        try
        {
            await socket.ConnectAsync(uri, ct);
            logger.LogInformation("EventSub connected to {Uri}; waiting for session_welcome.", uri);

            while (!ct.IsCancellationRequested)
            {
                string? message;
                using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    Task<string?>? receive = socketOpen
                        ? socket.ReceiveTextAsync(receiveCts.Token)
                        : null;

                    // During a handoff the reconnect grace period replaces the keepalive
                    // watchdog: Twitch does not promise keepalives on the old socket then.
                    Task watchdog = handoff?.Deadline
                        ?? Task.Delay(state.Keepalive + KeepaliveGrace, clock, timeoutCts.Token);

                    // Order matters when several are already complete: drain the old socket
                    // first, then prefer a finished handoff over an expired deadline.
                    var waits = new List<Task>(3);
                    if (receive is not null)
                        waits.Add(receive);
                    if (handoff is not null)
                        waits.Add(handoff.Welcomed);
                    waits.Add(watchdog);

                    Task first = await Task.WhenAny(waits);
                    await timeoutCts.CancelAsync();
                    if (first != receive)
                    {
                        if (receive is not null)
                        {
                            await receiveCts.CancelAsync();
                            await ObserveAsync(receive);
                        }

                        if (handoff is null)
                            throw new EventSubRestartException("keepalive timeout");

                        if (first != handoff.Welcomed)
                            throw new EventSubRestartException("reconnect handoff timed out");

                        // Throws when the new socket failed; the session then restarts fresh.
                        (string newSessionId, TimeSpan? keepalive) = await handoff.Welcomed;
                        state.Keepalive = keepalive ?? state.Keepalive;

                        // Bind the new session while the old socket is still open.
                        await session.OnReconnectedAsync(newSessionId, ct);

                        IEventSubSocket old = socket;
                        socket = await handoff.TakeSocketAsync();
                        socketOpen = true;
                        await handoff.DisposeAsync();
                        handoff = null;
                        await old.DisposeAsync();
                        logger.LogInformation(
                            "EventSub reconnect handoff complete; closed the old connection.");
                        continue;
                    }

                    message = await receive;
                }

                if (message is null)
                {
                    if (handoff is null)
                        return;

                    // Twitch closed the old socket first; keep waiting for the new welcome.
                    socketOpen = false;
                    continue;
                }

                Uri? reconnectUrl = await HandleMessageAsync(message, session, route, state, ct);
                if (reconnectUrl is null)
                    continue;

                if (handoff is not null)
                {
                    logger.LogInformation(
                        "Ignoring a second session_reconnect during a reconnect handoff.");
                    continue;
                }

                handoff = StartHandoff(reconnectUrl, ct);
                logger.LogInformation(
                    "EventSub reconnecting to {Uri}; the old connection stays open until session_welcome.",
                    reconnectUrl);
            }
        }
        finally
        {
            if (handoff is not null)
                await handoff.DisposeAsync();
            await socket.DisposeAsync();
        }
    }

    /// <summary>
    /// Opens the reconnect socket and waits for its <c>session_welcome</c>, while the caller keeps
    /// reading the old socket. The binding decides what the new session needs; WebSocket subscriptions move with it.
    /// </summary>
    private ReconnectHandoff StartHandoff(Uri reconnectUrl, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Task deadline = Task.Delay(ReconnectGrace, clock, cts.Token);
            IEventSubSocket socket = socketFactory.Create();
            Task<(string SessionId, TimeSpan? Keepalive)> welcomed =
                ReceiveHandoffWelcomeAsync(socket, reconnectUrl, cts.Token);
            return new ReconnectHandoff(socket, welcomed, deadline, cts);
        }
        catch
        {
            cts.Dispose();
            throw;
        }
    }

    /// <returns>
    /// The new session's ID and keepalive; the keepalive is null when the welcome does not state one.
    /// </returns>
    private static async Task<(string SessionId, TimeSpan? Keepalive)> ReceiveHandoffWelcomeAsync(
        IEventSubSocket socket, Uri reconnectUrl, CancellationToken ct)
    {
        await socket.ConnectAsync(reconnectUrl, ct);
        string? message = await socket.ReceiveTextAsync(ct)
            ?? throw new EventSubRestartException(
                "reconnect connection closed before session_welcome");

        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            if (root.GetProperty("metadata").GetProperty("message_type").GetString()
                != "session_welcome")
            {
                throw new EventSubRestartException(
                    "reconnect connection did not start with session_welcome");
            }

            return ReadWelcome(root.GetProperty("payload"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            throw new EventSubRestartException(
                "reconnect connection sent a malformed session_welcome");
        }
    }

    private static (string SessionId, TimeSpan? Keepalive) ReadWelcome(JsonElement payload)
    {
        JsonElement session = payload.GetProperty("session");
        string sessionId = session.GetProperty("id").GetString()!;
        TimeSpan? keepalive =
            session.TryGetProperty("keepalive_timeout_seconds", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
        return (sessionId, keepalive);
    }

    /// <returns>The reconnect URL when Twitch sent <c>session_reconnect</c>; otherwise null.</returns>
    private async Task<Uri?> HandleMessageAsync(
        string message,
        IEventSubSessionBinding session,
        StreamObservationRoute route,
        SessionState state,
        CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring malformed EventSub message.");
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string messageType = root.GetProperty("metadata").GetProperty("message_type")
                .GetString() ?? "";
            JsonElement payload = root.GetProperty("payload");

            switch (messageType)
            {
                case "session_welcome":
                    (string sessionId, TimeSpan? keepalive) = ReadWelcome(payload);
                    state.Keepalive = keepalive ?? DefaultKeepalive;

                    int covered = await session.OnWelcomeAsync(sessionId, ct);
                    state.HadSubscriptions |= covered > 0;
                    return null;

                case "notification":
                    await HandleNotificationAsync(payload, route, ct);
                    return null;

                case "session_keepalive":
                    return null;

                case "session_reconnect":
                    // Push coverage is unchanged: the old socket keeps delivering until the
                    // new one is welcomed, and the binding carries subscriptions over
                    // (WebSocket: they move with the session; conduit: the shard is rebound).
                    string? reconnect = payload.TryGetProperty("session", out JsonElement reconnectSession)
                        && reconnectSession.TryGetProperty("reconnect_url", out JsonElement url)
                            ? url.GetString()
                            : null;
                    if (reconnect is null)
                    {
                        schedule.SetPushStatus(PushStatus.Reconnecting);
                        throw new EventSubRestartException(
                            "Twitch requested a session reconnect without a reconnect_url.");
                    }

                    return new Uri(reconnect);

                case "revocation":
                    session.OnRevocation(payload);
                    return null;
            }

            return null;
        }
    }

    private async Task HandleNotificationAsync(
        JsonElement payload, StreamObservationRoute route, CancellationToken ct)
    {
        string type = payload.GetProperty("subscription").GetProperty("type").GetString() ?? "";
        if (type is not ("stream.online" or "stream.offline"))
            return;

        JsonElement @event = payload.GetProperty("event");
        string userId = @event.GetProperty("broadcaster_user_id").GetString() ?? "";
        ChannelEntry? channel = channels.Snapshot
            .FirstOrDefault(entry => entry.UserId == userId);
        if (channel is null)
            return;

        if (type == "stream.online")
        {
            string streamId = @event.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString() ?? ""
                : "";
            if (!TwitchStreamId.IsNumeric(streamId))
            {
                logger.LogWarning(
                    "EventSub stream.online for {Login} had no numeric stream id.", channel.Login);
                return;
            }

            DateTimeOffset started = @event.TryGetProperty("started_at", out JsonElement startedElement)
                && DateTimeOffset.TryParse(
                    startedElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsed)
                    ? parsed
                    : clock.GetUtcNow();

            ChannelInfo? info;
            try
            {
                info = await poller.GetChannelInfoAsync(userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "EventSub channel lookup failed for {Login}.", channel.Login);
                info = null;
            }

            await route(
                channel.Login,
                new StreamObservation.Live(
                    new LiveStream(
                        streamId,
                        info?.Title ?? "",
                        info?.Game ?? "",
                        started,
                        ThumbnailUrl: null)),
                ct);
        }
        else
        {
            await route(channel.Login, new StreamObservation.Offline(), ct);
        }

        schedule.Burst(BurstDuration);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    private sealed class SessionState
    {
        public TimeSpan Keepalive { get; set; } = DefaultKeepalive;
        public bool HadSubscriptions { get; set; }
        public bool SetupFailed { get; set; }
    }

    /// <summary>
    /// A reconnect socket waiting for its <c>session_welcome</c>, bounded by Twitch's grace period.
    /// It owns the socket until <see cref="TakeSocketAsync"/> hands it over.
    /// </summary>
    private sealed class ReconnectHandoff(
        IEventSubSocket socket,
        Task<(string SessionId, TimeSpan? Keepalive)> welcomed,
        Task deadline,
        CancellationTokenSource cts) : IAsyncDisposable
    {
        private IEventSubSocket? _socket = socket;

        public Task<(string SessionId, TimeSpan? Keepalive)> Welcomed { get; } = welcomed;

        public Task Deadline { get; } = deadline;

        public async Task<IEventSubSocket> TakeSocketAsync()
        {
            await cts.CancelAsync();
            await ObserveAsync(Deadline);
            IEventSubSocket taken = _socket
                ?? throw new InvalidOperationException("The handoff socket was already taken.");
            _socket = null;
            return taken;
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await ObserveAsync(Welcomed);
            await ObserveAsync(Deadline);
            if (_socket is not null)
            {
                await _socket.DisposeAsync();
                _socket = null;
            }

            cts.Dispose();
        }
    }
}
