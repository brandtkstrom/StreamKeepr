using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// EventSub WebSocket client. It waits for an admin token, subscribes stream.online/offline
/// for every watched channel with a user ID, drives all-or-nothing push coverage, routes
/// notifications to the channel coordinator, and recovers from disconnects and revocations.
/// </summary>
public sealed class EventSubClient(
    IEventSubSocketFactory socketFactory,
    IHttpClientFactory httpFactory,
    IAdminTokenStore adminTokens,
    ChannelStore channels,
    IStreamPoller poller,
    PollSchedule schedule,
    IRetryJitter jitter,
    IOptions<TwitchOptions> options,
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
    private static readonly TimeSpan SubscriptionRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChannelInfoTimeout = TimeSpan.FromSeconds(5);

    private const int RecentMessageIdCapacity = 256;

    // All EventSub message handling is serialized by RunSessionAsync, including
    // the old/new socket overlap during a reconnect handoff. No lock is needed.
    private readonly HashSet<string> _recentMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentMessageIdsOrder = new();

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
        TimeSpan backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            string? token;
            try
            {
                token = await adminTokens.GetAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read the admin token; polling continues.");
                schedule.SetPushStatus(PushStatus.Reconnecting);
                try
                {
                    await Task.Delay(InitialBackoff, clock, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (token is null)
            {
                schedule.SetPushStatus(PushStatus.NoAdminToken);
                try
                {
                    await adminTokens.WaitForTokenAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Waiting for the admin token failed; polling continues.");
                    try
                    {
                        await Task.Delay(InitialBackoff, clock, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }

                continue;
            }

            if (!HasResolvedChannel())
            {
                try
                {
                    await WaitForResolvedChannelAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                backoff = InitialBackoff;
                continue;
            }

            // The token exists, but no subscription is active until the session welcomes us.
            schedule.SetPushStatus(PushStatus.Reconnecting);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void Restart() => sessionCts.Cancel();

            channels.SubscriptionsChanged += Restart;
            adminTokens.Changed += Restart;

            var state = new SessionState();
            try
            {
                await RunSessionAsync(token, route, BaseUri, state, sessionCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
            {
                // Channel/token change: reconnect and recompute subscriptions.
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
                adminTokens.Changed -= Restart;
                schedule.SetPushStatus(PushStatus.Reconnecting);
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
        string token,
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
                        TimeSpan? keepalive = await handoff.Welcomed;
                        state.Keepalive = keepalive ?? state.Keepalive;

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

                Uri? reconnectUrl = await HandleMessageAsync(message, token, route, state, ct);
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
    /// reading the old socket. Subscriptions move with the session, so nothing is re-subscribed.
    /// </summary>
    private ReconnectHandoff StartHandoff(Uri reconnectUrl, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Task deadline = Task.Delay(ReconnectGrace, clock, cts.Token);
            IEventSubSocket socket = socketFactory.Create();
            Task<TimeSpan?> welcomed = ReceiveHandoffWelcomeAsync(socket, reconnectUrl, cts.Token);
            return new ReconnectHandoff(socket, welcomed, deadline, cts);
        }
        catch
        {
            cts.Dispose();
            throw;
        }
    }

    /// <returns>The new session's keepalive, or null when the welcome does not state one.</returns>
    private async Task<TimeSpan?> ReceiveHandoffWelcomeAsync(
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
            if (root.GetProperty("metadata").GetProperty("message_type").GetString()
                != "session_welcome")
            {
                throw new EventSubRestartException(
                    "reconnect connection did not start with session_welcome");
            }

            if (!TryReadWelcome(
                    root.GetProperty("payload"), out _, out TimeSpan? keepalive))
            {
                throw new EventSubRestartException(
                    "reconnect connection sent a malformed session_welcome");
            }

            return keepalive;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            throw new EventSubRestartException(
                "reconnect connection sent a malformed session_welcome");
        }
    }

    private static bool TryReadWelcome(
        JsonElement payload, out string sessionId, out TimeSpan? keepalive)
    {
        sessionId = "";
        keepalive = null;
        if (!payload.TryGetProperty("session", out JsonElement session)
            || session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("id", out JsonElement idElement)
            || idElement.GetString() is not { Length: > 0 } id)
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
        string token,
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
            if (!root.TryGetProperty("metadata", out JsonElement metadata)
                || !metadata.TryGetProperty("message_type", out JsonElement messageTypeElement))
            {
                logger.LogWarning("Ignoring EventSub message without metadata/message_type.");
                return null;
            }

            string messageType = messageTypeElement.GetString() ?? "";
            string? messageId = metadata.TryGetProperty("message_id", out JsonElement messageIdElement)
                ? messageIdElement.GetString()
                : null;
            if (IsDuplicateMessage(messageId))
            {
                logger.LogDebug("Ignoring duplicate EventSub message {MessageId}.", messageId);
                return null;
            }

            if (!root.TryGetProperty("payload", out JsonElement payload))
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

                    int covered = await SubscribeAllAsync(sessionId, token, ct);
                    state.HadSubscriptions |= covered > 0;
                    return null;

                case "notification":
                    await HandleNotificationAsync(payload, route, ct);
                    return null;

                case "session_keepalive":
                    return null;

                case "session_reconnect":
                    // Push coverage is unchanged: the old socket keeps delivering until the
                    // new one is welcomed, and subscriptions move with the session.
                    string? reconnect = payload.TryGetProperty("session", out JsonElement reconnectSession)
                        && reconnectSession.TryGetProperty("reconnect_url", out JsonElement url)
                            ? url.GetString()
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
                    string? revokedType = null;
                    string? revokedStatus = null;
                    if (payload.TryGetProperty("subscription", out JsonElement subscription)
                        && subscription.ValueKind == JsonValueKind.Object)
                    {
                        if (subscription.TryGetProperty("type", out JsonElement revokedTypeElement))
                            revokedType = revokedTypeElement.GetString();
                        if (subscription.TryGetProperty("status", out JsonElement revokedStatusElement))
                            revokedStatus = revokedStatusElement.GetString();
                    }

                    schedule.SetPushStatus(PushStatus.Reconnecting);
                    logger.LogWarning(
                        "EventSub subscription {Type} was revoked ({Status}); reconnecting.",
                        revokedType ?? "unknown",
                        revokedStatus ?? "unknown");
                    throw new EventSubRestartException("subscription revoked");
            }

            return null;
        }
    }

    private bool HasResolvedChannel() =>
        channels.Snapshot.Any(entry => entry.UserId is not null);

    private async Task WaitForResolvedChannelAsync(CancellationToken ct)
    {
        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => signal.TrySetResult();

        channels.SubscriptionsChanged += OnChanged;
        adminTokens.Changed += OnChanged;
        try
        {
            if (HasResolvedChannel())
                return;

            IReadOnlyList<ChannelEntry> snapshot = channels.Snapshot;
            schedule.SetPushStatus(
                snapshot.Count == 0
                    ? PushStatus.PollingOnly
                    : PushStatus.PartialCoverage);

            await signal.Task.WaitAsync(ct);
        }
        finally
        {
            channels.SubscriptionsChanged -= OnChanged;
            adminTokens.Changed -= OnChanged;
        }
    }

    private async Task<int> SubscribeAllAsync(
        string sessionId, string token, CancellationToken ct)
    {
        ChannelEntry[] watched = channels.Snapshot.ToArray();
        int covered = 0;

        string currentToken = token;
        foreach (ChannelEntry channel in watched)
        {
            if (channel.UserId is null)
                continue;

            SubscriptionAttempt online = await SubscribeAsync(
                sessionId, currentToken, channel, "stream.online", ct);
            currentToken = online.Token;
            if (online.Result == SubscriptionResult.Capacity)
            {
                logger.LogWarning(
                    "EventSub push capacity reached for {Login}; the remaining channels stay on polling.",
                    channel.Login);
                break;
            }

            if (online.Result == SubscriptionResult.Failed)
            {
                schedule.SetPushStatus(PushStatus.Reconnecting);
                throw new EventSubRestartException(
                    $"subscription request failed for {channel.Login}");
            }

            SubscriptionAttempt offline = await SubscribeAsync(
                sessionId, currentToken, channel, "stream.offline", ct);
            currentToken = offline.Token;
            if (offline.Result == SubscriptionResult.Capacity)
            {
                logger.LogWarning(
                    "EventSub push capacity reached for {Login}; keeping the online subscription and leaving the remaining channels on polling.",
                    channel.Login);
                break;
            }

            if (offline.Result == SubscriptionResult.Failed)
            {
                schedule.SetPushStatus(PushStatus.Reconnecting);
                throw new EventSubRestartException(
                    $"subscription request failed for {channel.Login}");
            }

            if (online.Result == SubscriptionResult.Active
                && offline.Result == SubscriptionResult.Active)
            {
                covered++;
            }
        }

        if (covered == 0 && watched.Length == 0)
            logger.LogInformation("EventSub has no watched channels to cover.");

        schedule.SetPushStatus(
            watched.Length == 0 ? PushStatus.PollingOnly
            : covered == watched.Length ? PushStatus.AllConnected
            : PushStatus.PartialCoverage);
        logger.LogInformation(
            "EventSub push coverage: {Covered}/{Total} channels.",
            covered,
            watched.Length);
        return covered;
    }

    private async Task<SubscriptionAttempt> SubscribeAsync(
        string sessionId,
        string token,
        ChannelEntry channel,
        string type,
        CancellationToken ct)
    {
        SubscriptionAttempt attempt = await SendSubscriptionAsync(
            sessionId, token, channel, type, ct);

        if (attempt.Result != SubscriptionResult.Unauthorized)
            return attempt;

        string? refreshed = await RefreshAdminTokenAsync(channel.Login, ct);
        if (refreshed is null)
        {
            logger.LogWarning(
                "EventSub admin-token refresh returned no token for {Login}.", channel.Login);
            return attempt with { Result = SubscriptionResult.Failed };
        }

        SubscriptionAttempt retry = await SendSubscriptionAsync(
            sessionId, refreshed, channel, type, ct);
        if (retry.Result == SubscriptionResult.Unauthorized)
        {
            logger.LogError(
                "EventSub subscription {Type} for {Login} still returned 401 after a forced token refresh; check the Twitch Client-Id and token type.",
                type,
                channel.Login);
            return retry with { Result = SubscriptionResult.Failed };
        }

        return retry;
    }

    private async Task<string?> RefreshAdminTokenAsync(string login, CancellationToken ct)
    {
        try
        {
            return await adminTokens.RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "EventSub admin-token refresh failed for {Login}.", login);
            return null;
        }
    }

    private async Task<SubscriptionAttempt> VerifyConflictAsync(
        string sessionId,
        string token,
        ChannelEntry channel,
        string type,
        CancellationToken ct)
    {
        (bool matched, bool unauthorized) = await QuerySessionOwnershipAsync(
            token, channel.UserId!, sessionId, type, ct);

        if (unauthorized)
        {
            string? refreshed = await RefreshAdminTokenAsync(channel.Login, ct);
            if (refreshed is null)
                return new SubscriptionAttempt(SubscriptionResult.Failed, token);

            token = refreshed;
            (matched, unauthorized) = await QuerySessionOwnershipAsync(
                token, channel.UserId!, sessionId, type, ct);
            if (unauthorized)
            {
                logger.LogError(
                    "EventSub 409 verification for {Type} for {Login} still returned 401 after a forced token refresh.",
                    type,
                    channel.Login);
                return new SubscriptionAttempt(SubscriptionResult.Failed, token);
            }
        }

        return new SubscriptionAttempt(
            matched ? SubscriptionResult.Active : SubscriptionResult.Refused,
            token);
    }

    private async Task<(bool Matched, bool Unauthorized)> QuerySessionOwnershipAsync(
        string token,
        string userId,
        string sessionId,
        string type,
        CancellationToken ct)
    {
        try
        {
            using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Helix);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"helix/eventsub/subscriptions?user_id={Uri.EscapeDataString(userId)}");
            request.Headers.Add("Client-Id", options.Value.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var requestTimeout = new CancellationTokenSource(
                SubscriptionRequestTimeout, clock);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct, requestTimeout.Token);
            using HttpResponseMessage response = await http.SendAsync(request, requestCts.Token);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, true);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "EventSub 409 verification for {Type} for user {UserId} failed with HTTP {Status}; leaving the channel uncovered.",
                    type,
                    userId,
                    (int)response.StatusCode);
                return (false, false);
            }

            HelixEventSubSubscriptionsResponse? payload = await response.Content.ReadFromJsonAsync(
                TwitchJsonContext.Default.HelixEventSubSubscriptionsResponse, ct);
            bool matched = payload?.Data?.Any(subscription =>
                string.Equals(subscription.Status, "enabled", StringComparison.Ordinal)
                && string.Equals(subscription.Type, type, StringComparison.Ordinal)
                && subscription.Condition is { } condition
                && string.Equals(condition.BroadcasterUserId, userId, StringComparison.Ordinal)
                && subscription.Transport is { } transport
                && string.Equals(transport.Method, "websocket", StringComparison.Ordinal)
                && string.Equals(transport.SessionId, sessionId, StringComparison.Ordinal)) == true;

            if (!matched)
            {
                logger.LogWarning(
                    "EventSub 409 for {Type} for user {UserId} is not owned by session {SessionId}; leaving the channel uncovered.",
                    type,
                    userId,
                    sessionId);
            }

            return (matched, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "EventSub 409 verification for {Type} for user {UserId} failed; leaving the channel uncovered.",
                type,
                userId);
            return (false, false);
        }
    }

    private async Task<SubscriptionAttempt> SendSubscriptionAsync(
        string sessionId,
        string token,
        ChannelEntry channel,
        string type,
        CancellationToken ct)
    {
        var body = new EventSubSubscriptionRequest(
            type,
            "1",
            new EventSubCondition(channel.UserId!),
            new EventSubTransport("websocket", sessionId));

        try
        {
            using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Helix);
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "helix/eventsub/subscriptions")
            {
                Content = JsonContent.Create(
                    body, TwitchJsonContext.Default.EventSubSubscriptionRequest),
            };
            request.Headers.Add("Client-Id", options.Value.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var requestTimeout = new CancellationTokenSource(
                SubscriptionRequestTimeout, clock);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct, requestTimeout.Token);
            using HttpResponseMessage response = await http.SendAsync(request, requestCts.Token);
            if (response.IsSuccessStatusCode)
                return new SubscriptionAttempt(SubscriptionResult.Active, token);

            if (response.StatusCode == HttpStatusCode.Conflict)
                return await VerifyConflictAsync(sessionId, token, channel, type, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new SubscriptionAttempt(SubscriptionResult.Unauthorized, token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                string? detail = null;
                try
                {
                    detail = await response.Content.ReadAsStringAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // The status code is the authoritative signal; the body is diagnostic.
                }

                logger.LogWarning(
                    "EventSub subscription capacity reached for {Type} for {Login}: HTTP 429 {Detail}",
                    type,
                    channel.Login,
                    detail);
                return new SubscriptionAttempt(SubscriptionResult.Capacity, token);
            }

            if ((int)response.StatusCode >= 500)
            {
                logger.LogWarning(
                    "EventSub subscription {Type} for {Login} failed with HTTP {Status}.",
                    type,
                    channel.Login,
                    (int)response.StatusCode);
                return new SubscriptionAttempt(SubscriptionResult.Failed, token);
            }

            logger.LogWarning(
                "EventSub subscription {Type} for {Login} was refused with HTTP {Status}.",
                type,
                channel.Login,
                (int)response.StatusCode);
            return new SubscriptionAttempt(SubscriptionResult.Refused, token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "EventSub subscription {Type} for {Login} failed.", type, channel.Login);
            return new SubscriptionAttempt(SubscriptionResult.Failed, token);
        }
    }

    private async Task HandleNotificationAsync(
        JsonElement payload, StreamObservationRoute route, CancellationToken ct)
    {
        if (!payload.TryGetProperty("subscription", out JsonElement subscription)
            || subscription.ValueKind != JsonValueKind.Object
            || !subscription.TryGetProperty("type", out JsonElement typeElement))
        {
            logger.LogWarning("Ignoring EventSub notification without a subscription type.");
            return;
        }

        string type = typeElement.GetString() ?? "";
        if (type is not ("stream.online" or "stream.offline"))
            return;

        if (!payload.TryGetProperty("event", out JsonElement @event)
            || @event.ValueKind != JsonValueKind.Object
            || !@event.TryGetProperty("broadcaster_user_id", out JsonElement userIdElement))
        {
            logger.LogWarning(
                "Ignoring EventSub {Type} notification without a broadcaster user id.", type);
            return;
        }

        string userId = userIdElement.GetString() ?? "";
        if (userId.Length == 0)
        {
            logger.LogWarning(
                "Ignoring EventSub {Type} notification with an empty broadcaster user id.", type);
            return;
        }

        ChannelEntry? channel = channels.Snapshot
            .FirstOrDefault(entry => entry.UserId == userId);
        if (channel is null)
            return;

        if (type == "stream.online")
        {
            string? streamType = @event.TryGetProperty("type", out JsonElement streamTypeElement)
                ? streamTypeElement.GetString()
                : null;
            if (!string.Equals(streamType, "live", StringComparison.Ordinal))
            {
                if (streamType is null)
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
                        streamType);
                }

                await RouteAsync(route, channel.Login, new StreamObservation.Offline(), ct);
                schedule.Burst(BurstDuration);
                return;
            }

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

    private bool IsDuplicateMessage(string? messageId)
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
    }

    private enum SubscriptionResult
    {
        Active,
        Refused,
        Failed,
        Unauthorized,
        Capacity,
    }

    private sealed record SubscriptionAttempt(SubscriptionResult Result, string Token);

    private sealed class EventSubRestartException(string message) : Exception(message);

    /// <summary>
    /// A reconnect socket waiting for its <c>session_welcome</c>, bounded by Twitch's grace period.
    /// It owns the socket until <see cref="TakeSocketAsync"/> hands it over.
    /// </summary>
    private sealed class ReconnectHandoff(
        IEventSubSocket socket,
        Task<TimeSpan?> welcomed,
        Task deadline,
        CancellationTokenSource cts) : IAsyncDisposable
    {
        private IEventSubSocket? _socket = socket;

        public Task<TimeSpan?> Welcomed { get; } = welcomed;

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
