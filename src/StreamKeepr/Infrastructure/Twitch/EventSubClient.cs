using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// EventSub WebSocket client. It waits for an admin token, subscribes stream.online/offline
/// for every watched channel with a user ID, drives all-or-nothing push coverage, and routes
/// notifications to the channel coordinator.
/// </summary>
public sealed class EventSubClient(
    IEventSubSocketFactory socketFactory,
    IHttpClientFactory httpFactory,
    IAdminTokenStore adminTokens,
    ChannelStore channels,
    IStreamPoller poller,
    PollSchedule schedule,
    IOptions<TwitchOptions> options,
    TimeProvider clock,
    ILogger<EventSubClient> logger) : IEventSubClient
{
    private const string EventSubUri = "wss://eventsub.wss.twitch.tv/ws";
    private const string Online = "stream.online";
    private const string Offline = "stream.offline";
    private static readonly TimeSpan BurstDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    public async Task RunAsync(StreamObservationRoute route, CancellationToken ct)
    {
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

                continue;
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void Restart() => sessionCts.Cancel();

            channels.SubscriptionsChanged += Restart;
            adminTokens.Changed += Restart;

            try
            {
                await RunSessionAsync(token, route, sessionCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
            {
                // Channel/token change: reconnect and recompute subscriptions.
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

            if (ct.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(ReconnectDelay, clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(
        string token, StreamObservationRoute route, CancellationToken ct)
    {
        await using IEventSubSocket socket = socketFactory.Create();
        await socket.ConnectAsync(new Uri(EventSubUri), ct);
        logger.LogInformation("EventSub connected; waiting for session_welcome.");

        while (!ct.IsCancellationRequested)
        {
            string? message = await socket.ReceiveTextAsync(ct);
            if (message is null)
                return;

            await HandleMessageAsync(message, token, route, ct);
        }
    }

    private async Task HandleMessageAsync(
        string message, string token, StreamObservationRoute route, CancellationToken ct)
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

            switch (messageType)
            {
                case "session_welcome":
                    string sessionId = root.GetProperty("payload")
                        .GetProperty("session")
                        .GetProperty("id")
                        .GetString()!;
                    await SubscribeAllAsync(sessionId, token, ct);
                    break;

                case "notification":
                    await HandleNotificationAsync(root.GetProperty("payload"), route, ct);
                    break;

                case "session_keepalive":
                    break;

                case "session_reconnect":
                case "revocation":
                    schedule.SetPushCoverage(false);
                    throw new EventSubRestartException($"EventSub {messageType}.");
            }
        }
    }

    private async Task SubscribeAllAsync(
        string sessionId, string token, CancellationToken ct)
    {
        ChannelEntry[] watched = channels.Snapshot.ToArray();
        int covered = 0;

        foreach (ChannelEntry channel in watched)
        {
            if (channel.UserId is null)
                continue;

            bool online = await SubscribeAsync(sessionId, token, channel, Online, ct);
            bool offline = await SubscribeAsync(sessionId, token, channel, Offline, ct);
            if (online && offline)
                covered++;
        }

        if (covered == 0 && watched.Length == 0)
            logger.LogInformation("EventSub has no watched channels to cover.");

        schedule.SetPushCoverage(watched.Length > 0 && covered == watched.Length);
        logger.LogInformation(
            "EventSub push coverage: {Covered}/{Total} channels.",
            covered,
            watched.Length);
    }

    private async Task<bool> SubscribeAsync(
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
                return true;

            logger.LogWarning(
                "EventSub subscription {Type} for {Login} was refused with HTTP {Status}.",
                type,
                channel.Login,
                (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "EventSub subscription {Type} for {Login} failed.", type, channel.Login);
            return false;
        }
    }

    private async Task HandleNotificationAsync(
        JsonElement payload, StreamObservationRoute route, CancellationToken ct)
    {
        string type = payload.GetProperty("subscription").GetProperty("type").GetString() ?? "";
        if (type is not (Online or Offline))
            return;

        JsonElement @event = payload.GetProperty("event");
        string userId = @event.GetProperty("broadcaster_user_id").GetString() ?? "";
        ChannelEntry? channel = channels.Snapshot
            .FirstOrDefault(entry => entry.UserId == userId);
        if (channel is null)
            return;

        if (type == Online)
        {
            string streamId = @event.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString() ?? ""
                : "";
            if (!IsNumericId(streamId))
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

    private static bool IsNumericId(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);

    private sealed class EventSubRestartException(string message) : Exception(message);
}
