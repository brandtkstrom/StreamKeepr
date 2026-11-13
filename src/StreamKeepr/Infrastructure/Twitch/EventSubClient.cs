using System.Globalization;
using System.Text.Json;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// EventSub client core. It runs the WebSocket session loop (keepalive, reconnect handoff,
/// backoff), dedupes and routes notifications to the channel coordinator, and recovers from
/// disconnects. The <see cref="IEventSubBinding"/> decides how a session authenticates and
/// subscribes: a user-token WebSocket or a conduit shard.
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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChannelInfoTimeout = TimeSpan.FromSeconds(5);

    private const int RecentMessageIdCapacity = 256;

    // All EventSub message handling is serialized by RunSessionAsync, including
    // the old/new socket overlap during a reconnect handoff. No lock is needed.
    private readonly HashSet<string> _recentMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentMessageIdsOrder = new();

    public string Transport => binding.Transport;

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
        TimeSpan backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
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
                if (!await DelayAsync(NextBackoff(ref backoff), ct))
                    break;
                continue;
            }

            if (session is null)
            {
                // The binding already waited (for an admin token or a resolved channel).
                backoff = InitialBackoff;
                continue;
            }

            // Setup succeeded, but no subscription delivers until the session welcomes us.
            schedule.SetPushStatus(PushStatus.Reconnecting);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void Restart() => sessionCts.Cancel();

            channels.SubscriptionsChanged += Restart;
            binding.RestartRequested += Restart;

            var state = new SessionState();
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

            if (state.HadSubscriptions)
                backoff = InitialBackoff;

            if (ct.IsCancellationRequested)
                break;

            // A voluntary restart (channel or binding change) reconnects without backoff.
            if (sessionCts.IsCancellationRequested)
            {
                backoff = InitialBackoff;
                continue;
            }

            if (!await DelayAsync(NextBackoff(ref backoff), ct))
                break;
        }
    }

    /// <summary>Returns the jittered delay for this attempt and doubles the base for the next.</summary>
    private TimeSpan NextBackoff(ref TimeSpan backoff)
    {
        TimeSpan delay = jitter.Apply(backoff);
        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        return delay;
    }

    /// <returns>False when <paramref name="ct"/> was cancelled during the delay.</returns>
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, clock, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
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
            using var connectTimeout = new CancellationTokenSource(ConnectTimeout, clock);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct, connectTimeout.Token);
            await socket.ConnectAsync(uri, connectCts.Token);
            logger.LogInformation(
                "EventSub connected to {Uri}; waiting for session_welcome.",
                uri.GetLeftPart(UriPartial.Path));

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

                    if (first == receive)
                    {
                        message = await receive;
                    }
                    else if (receive is { IsCompletedSuccessfully: true }
                        && receive.Result is not null)
                    {
                        // The old socket delivered a message at the same instant as the
                        // watchdog; drain it before considering the timeout.
                        message = receive.Result;
                    }
                    else
                    {
                        if (receive is not null)
                        {
                            await receiveCts.CancelAsync();
                            await ObserveAsync(receive);
                        }

                        if (handoff is null)
                            throw new EventSubRestartException("keepalive timeout");

                        if (!handoff.Welcomed.IsCompleted)
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
                }

                if (message is not null)
                    schedule.EventSubMessageReceived();

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
                    reconnectUrl.GetLeftPart(UriPartial.Path));
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
    /// reading the old socket. The binding decides what the new session needs: WebSocket
    /// subscriptions move with it; a conduit shard is rebound.
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
            return new ReconnectHandoff(socket, welcomed, deadline, cts, ObserveAsync);
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
    private async Task<(string SessionId, TimeSpan? Keepalive)> ReceiveHandoffWelcomeAsync(
        IEventSubSocket socket, Uri reconnectUrl, CancellationToken ct)
    {
        using var connectTimeout = new CancellationTokenSource(ConnectTimeout, clock);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct, connectTimeout.Token);
        try
        {
            await socket.ConnectAsync(reconnectUrl, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EventSubRestartException("reconnect connect timed out");
        }

        string? message = await socket.ReceiveTextAsync(ct)
            ?? throw new EventSubRestartException(
                "reconnect connection closed before session_welcome");

        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetObjectProperty(root, "metadata", out JsonElement metadata)
                || !TryGetStringProperty(metadata, "message_type", out string? messageType)
                || messageType != "session_welcome")
            {
                throw new EventSubRestartException(
                    "reconnect connection did not start with session_welcome");
            }

            if (!TryGetObjectProperty(root, "payload", out JsonElement payload)
                || !TryReadWelcome(payload, out string sessionId, out TimeSpan? keepalive))
            {
                throw new EventSubRestartException(
                    "reconnect connection sent a malformed session_welcome");
            }

            return (sessionId, keepalive);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            throw new EventSubRestartException(
                "reconnect connection sent a malformed session_welcome");
        }
    }

    private static bool TryGetObjectProperty(
        JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetStringProperty(
        JsonElement parent, string name, out string? value)
    {
        value = null;
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadWelcome(
        JsonElement payload, out string sessionId, out TimeSpan? keepalive)
    {
        sessionId = "";
        keepalive = null;
        if (!TryGetObjectProperty(payload, "session", out JsonElement session)
            || !TryGetStringProperty(session, "id", out string? id)
            || string.IsNullOrEmpty(id))
        {
            return false;
        }

        sessionId = id;
        if (session.TryGetProperty("keepalive_timeout_seconds", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int seconds)
            && seconds > 0)
        {
            keepalive = TimeSpan.FromSeconds(seconds);
        }

        return true;
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
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetObjectProperty(root, "metadata", out JsonElement metadata)
                || !TryGetStringProperty(metadata, "message_type", out string? messageType)
                || string.IsNullOrEmpty(messageType))
            {
                logger.LogWarning("Ignoring EventSub message without metadata/message_type.");
                return null;
            }

            TryGetStringProperty(metadata, "message_id", out string? messageId);
            if (TryMarkSeen(messageId))
            {
                logger.LogDebug("Ignoring duplicate EventSub message {MessageId}.", messageId);
                return null;
            }

            if (!TryGetObjectProperty(root, "payload", out JsonElement payload))
            {
                logger.LogWarning(
                    "Ignoring EventSub {MessageType} without a payload.", messageType);
                return null;
            }

            switch (messageType)
            {
                case "session_welcome":
                    if (!TryReadWelcome(payload, out string sessionId, out TimeSpan? keepalive))
                    {
                        logger.LogWarning(
                            "Ignoring EventSub session_welcome without a session id.");
                        return null;
                    }

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
                    string? reconnect = TryGetObjectProperty(
                            payload, "session", out JsonElement reconnectSession)
                        && TryGetStringProperty(
                            reconnectSession, "reconnect_url", out string? reconnectUrl)
                            ? reconnectUrl
                            : null;
                    if (!Uri.TryCreate(reconnect, UriKind.Absolute, out Uri? reconnectUri)
                        || reconnectUri.Scheme != "wss")
                    {
                        logger.LogWarning(
                            "EventSub session_reconnect had an invalid reconnect_url.");
                        throw new EventSubRestartException("invalid reconnect URL");
                    }

                    return reconnectUri;

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
        string? type = null;
        if (!TryGetObjectProperty(payload, "subscription", out JsonElement subscription)
            || !TryGetStringProperty(subscription, "type", out type)
            || string.IsNullOrEmpty(type))
        {
            logger.LogWarning("Ignoring EventSub notification without a subscription type.");
            return;
        }

        if (type is not ("stream.online" or "stream.offline"))
            return;

        string? userId = null;
        if (!TryGetObjectProperty(payload, "event", out JsonElement @event)
            || !TryGetStringProperty(@event, "broadcaster_user_id", out userId)
            || string.IsNullOrEmpty(userId))
        {
            logger.LogWarning(
                "Ignoring EventSub {Type} notification without a broadcaster user id.", type);
            return;
        }

        ChannelEntry? channel = channels.Snapshot
            .FirstOrDefault(entry => entry.UserId == userId);
        if (channel is null)
            return;

        if (type == "stream.online")
        {
            string? broadcastType = null;
            TryGetStringProperty(@event, "type", out broadcastType);
            if (!string.Equals(broadcastType, "live", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(broadcastType))
                {
                    logger.LogWarning(
                        "EventSub stream.online for {Login} had no broadcast type; treating it as offline.",
                        channel.Login);
                }
                else
                {
                    logger.LogInformation(
                        "EventSub stream.online for {Login} had broadcast type {Type}; treating it as offline.",
                        channel.Login,
                        broadcastType);
                }

                await RouteAsync(route, channel.Login, new StreamObservation.Offline(), ct);
                schedule.Burst(BurstDuration);
                return;
            }

            TryGetStringProperty(@event, "id", out string? streamIdValue);
            string streamId = streamIdValue ?? "";
            if (!TwitchStreamId.IsNumeric(streamId))
            {
                logger.LogWarning(
                    "EventSub stream.online for {Login} had no numeric stream id.", channel.Login);
                return;
            }

            TryGetStringProperty(@event, "started_at", out string? startedText);
            DateTimeOffset started = DateTimeOffset.TryParse(
                startedText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed)
                    ? parsed
                    : clock.GetUtcNow();

            ChannelInfo? info;
            try
            {
                using var lookupTimeout = new CancellationTokenSource(
                    ChannelInfoTimeout, clock);
                using var lookupCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, lookupTimeout.Token);
                info = await poller.GetChannelInfoAsync(userId, lookupCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "EventSub channel lookup timed out for {Login}.", channel.Login);
                info = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EventSub channel lookup failed for {Login}.", channel.Login);
                info = null;
            }

            await RouteAsync(
                route,
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
            await RouteAsync(route, channel.Login, new StreamObservation.Offline(), ct);
        }

        schedule.Burst(BurstDuration);
    }

    private async Task RouteAsync(
        StreamObservationRoute route,
        string login,
        StreamObservation observation,
        CancellationToken ct)
    {
        try
        {
            await route(login, observation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EventSub route failed for {Login}.", login);
        }
    }

    /// <returns>True when the ID was already seen; otherwise records it and returns false.</returns>
    private bool TryMarkSeen(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return false;

        if (_recentMessageIds.Add(messageId))
        {
            _recentMessageIdsOrder.Enqueue(messageId);
            while (_recentMessageIdsOrder.Count > RecentMessageIdCapacity)
                _recentMessageIds.Remove(_recentMessageIdsOrder.Dequeue());
            return false;
        }

        return true;
    }

    private async Task ObserveAsync(Task task)
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
            logger.LogDebug(ex, "EventSub socket operation failed while being observed.");
        }
    }

    private sealed class SessionState
    {
        public TimeSpan Keepalive { get; set; } = DefaultKeepalive;
        public bool HadSubscriptions { get; set; }
    }

    /// <summary>
    /// A reconnect socket waiting for its <c>session_welcome</c>, bounded by Twitch's grace period.
    /// It owns the socket until <see cref="TakeSocketAsync"/> hands it over.
    /// </summary>
    private sealed class ReconnectHandoff(
        IEventSubSocket socket,
        Task<(string SessionId, TimeSpan? Keepalive)> welcomed,
        Task deadline,
        CancellationTokenSource cts,
        Func<Task, Task> observe) : IAsyncDisposable
    {
        private IEventSubSocket? _socket = socket;

        public Task<(string SessionId, TimeSpan? Keepalive)> Welcomed { get; } = welcomed;

        public Task Deadline { get; } = deadline;

        public async Task<IEventSubSocket> TakeSocketAsync()
        {
            await cts.CancelAsync();
            await observe(Deadline);
            IEventSubSocket taken = _socket
                ?? throw new InvalidOperationException("The handoff socket was already taken.");
            _socket = null;
            return taken;
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await observe(Welcomed);
            await observe(Deadline);
            if (_socket is not null)
            {
                await _socket.DisposeAsync();
                _socket = null;
            }

            cts.Dispose();
        }
    }
}
