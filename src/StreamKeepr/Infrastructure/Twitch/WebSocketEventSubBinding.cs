using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// User-token WebSocket transport (contract §5): waits for an admin token, subscribes
/// stream.online/offline for every watched channel on each new session, and restarts on
/// revocation or admin token change.
/// </summary>
public sealed class WebSocketEventSubBinding(
    IHttpClientFactory httpFactory,
    IAdminTokenStore adminTokens,
    ChannelStore channels,
    PollSchedule schedule,
    IOptions<TwitchOptions> options,
    TimeProvider clock,
    ILogger<WebSocketEventSubBinding> logger) : IEventSubBinding
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public event Action? RestartRequested
    {
        add => adminTokens.Changed += value;
        remove => adminTokens.Changed -= value;
    }

    public async Task<IEventSubSessionBinding?> BeginSessionAsync(CancellationToken ct)
    {
        string? token;
        try
        {
            token = await adminTokens.GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the admin token; polling continues.");
            schedule.SetPushStatus(PushStatus.Reconnecting);
            await Task.Delay(RetryDelay, clock, ct);
            return null;
        }

        if (token is not null)
            return new Session(this, token);

        schedule.SetPushStatus(PushStatus.NoAdminToken);
        try
        {
            await adminTokens.WaitForTokenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Waiting for the admin token failed; polling continues.");
            await Task.Delay(RetryDelay, clock, ct);
        }

        return null;
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
                schedule.SetPushStatus(PushStatus.Reconnecting);
                throw new EventSubRestartException(
                    $"subscription request failed for {channel.Login}");
            }

            if (online == SubscriptionResult.Active && offline == SubscriptionResult.Active)
                covered++;
        }

        if (covered == 0 && watched.Length == 0)
            logger.LogInformation("EventSub has no watched channels to cover.");

        schedule.SetPushStatus(PushStatus.FromCoverage(covered, watched.Length));
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

    private enum SubscriptionResult
    {
        Active,
        Refused,
        Failed,
    }

    private sealed class Session(WebSocketEventSubBinding owner, string token)
        : IEventSubSessionBinding
    {
        public Task<int> OnWelcomeAsync(string sessionId, CancellationToken ct) =>
            owner.SubscribeAllAsync(sessionId, token, ct);

        // Subscriptions move with the session, so the new socket needs nothing.
        public Task OnReconnectedAsync(string sessionId, CancellationToken ct) =>
            Task.CompletedTask;

        public void OnRevocation(JsonElement payload) => owner.Revoked();
    }

    private void Revoked()
    {
        schedule.SetPushStatus(PushStatus.Reconnecting);
        logger.LogWarning("EventSub subscription was revoked; reconnecting.");
        throw new EventSubRestartException("subscription revoked");
    }
}
