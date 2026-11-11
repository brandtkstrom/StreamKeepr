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

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
        Uri uri = BaseUri;
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
                schedule.SetPushCoverage(false);
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
                schedule.SetPushCoverage(false);
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

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void Restart() => sessionCts.Cancel();

            channels.SubscriptionsChanged += Restart;
            adminTokens.Changed += Restart;

            var state = new SessionState();
            try
            {
                await RunSessionAsync(token, route, uri, state, sessionCts.Token);
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
                state.ReconnectUrl = ex.ReconnectUrl;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EventSub session failed.");
            }
            finally
            {
                channels.SubscriptionsChanged -= Restart;
                adminTokens.Changed -= Restart;
                schedule.SetPushCoverage(false);
            }

            if (state.HadSubscriptions)
                backoff = InitialBackoff;

            if (ct.IsCancellationRequested)
                break;

            if (state.ReconnectUrl is { } reconnect)
            {
                uri = reconnect;
                continue;
            }

            uri = BaseUri;
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
        await using IEventSubSocket socket = socketFactory.Create();
        await socket.ConnectAsync(uri, ct);
        logger.LogInformation("EventSub connected to {Uri}; waiting for session_welcome.", uri);

        while (!ct.IsCancellationRequested)
        {
            string? message;
            using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task<string?> receive = socket.ReceiveTextAsync(receiveCts.Token);
                Task timeout = Task.Delay(
                    state.Keepalive + KeepaliveGrace, clock, timeoutCts.Token);

                Task first = await Task.WhenAny(receive, timeout);
                if (first == timeout)
                {
                    await receiveCts.CancelAsync();
                    await ObserveAsync(receive);
                    throw new EventSubRestartException(
                        null, "keepalive timeout");
                }

                await timeoutCts.CancelAsync();
                message = await receive;
            }

            if (message is null)
                return;

            await HandleMessageAsync(message, token, route, state, ct);
        }
    }

    private async Task HandleMessageAsync(
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
            return;
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
                    JsonElement session = payload.GetProperty("session");
                    string sessionId = session.GetProperty("id").GetString()!;
                    if (session.TryGetProperty("keepalive_timeout_seconds", out JsonElement keepalive)
                        && keepalive.TryGetInt32(out int seconds)
                        && seconds > 0)
                    {
                        state.Keepalive = TimeSpan.FromSeconds(seconds);
                    }
                    else
                    {
                        state.Keepalive = DefaultKeepalive;
                    }

                    int covered = await SubscribeAllAsync(sessionId, token, ct);
                    state.HadSubscriptions |= covered > 0;
                    break;

                case "notification":
                    await HandleNotificationAsync(payload, route, ct);
                    break;

                case "session_keepalive":
                    break;

                case "session_reconnect":
                    schedule.SetPushCoverage(false);
                    string? reconnect = payload.TryGetProperty("session", out JsonElement reconnectSession)
                        ? reconnectSession.TryGetProperty("reconnect_url", out JsonElement url)
                            ? url.GetString()
                            : null
                        : null;
                    throw new EventSubRestartException(
                        reconnect is null ? null : new Uri(reconnect),
                        "Twitch requested a session reconnect.");

                case "revocation":
                    schedule.SetPushCoverage(false);
                    logger.LogWarning(
                        "EventSub subscription was revoked; reconnecting.");
                    throw new EventSubRestartException(null, "subscription revoked");
            }
        }
    }

    private async Task<int> SubscribeAllAsync(
        string sessionId, string token, CancellationToken ct)
    {
        ChannelEntry[] watched = channels.Snapshot.ToArray();
        int covered = 0;

        foreach (ChannelEntry channel in watched)
        {
            if (channel.UserId is null)
                continue;

            SubscriptionResult online = await SubscribeAsync(
                sessionId, token, channel, "stream.online", ct);
            SubscriptionResult offline = await SubscribeAsync(
                sessionId, token, channel, "stream.offline", ct);

            if (online == SubscriptionResult.Failed || offline == SubscriptionResult.Failed)
            {
                schedule.SetPushCoverage(false);
                throw new EventSubRestartException(
                    null, $"subscription request failed for {channel.Login}");
            }

            if (online == SubscriptionResult.Active && offline == SubscriptionResult.Active)
                covered++;
        }

        if (covered == 0 && watched.Length == 0)
            logger.LogInformation("EventSub has no watched channels to cover.");

        schedule.SetPushCoverage(watched.Length > 0 && covered == watched.Length);
        logger.LogInformation(
            "EventSub push coverage: {Covered}/{Total} channels.",
            covered,
            watched.Length);
        return covered;
    }

    private async Task<SubscriptionResult> SubscribeAsync(
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

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict || response.IsSuccessStatusCode)
                return SubscriptionResult.Active;

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
            {
                logger.LogWarning(
                    "EventSub subscription {Type} for {Login} failed with HTTP {Status}.",
                    type,
                    channel.Login,
                    (int)response.StatusCode);
                return SubscriptionResult.Failed;
            }

            logger.LogWarning(
                "EventSub subscription {Type} for {Login} was refused with HTTP {Status}.",
                type,
                channel.Login,
                (int)response.StatusCode);
            return SubscriptionResult.Refused;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "EventSub subscription {Type} for {Login} failed.", type, channel.Login);
            return SubscriptionResult.Failed;
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
        public Uri? ReconnectUrl { get; set; }
    }

    private enum SubscriptionResult
    {
        Active,
        Refused,
        Failed,
    }

    private sealed class EventSubRestartException(Uri? reconnectUrl, string message)
        : Exception(message)
    {
        public Uri? ReconnectUrl { get; } = reconnectUrl;
    }
}
