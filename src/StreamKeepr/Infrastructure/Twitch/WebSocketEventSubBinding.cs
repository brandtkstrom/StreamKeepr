using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// User-token WebSocket transport (contract §5.1): waits for an admin token and a resolved
/// channel, subscribes stream.online/offline for every watched channel on each new session,
/// and restarts on revocation or admin token change.
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
    private static readonly TimeSpan SubscriptionRequestTimeout = TimeSpan.FromSeconds(10);

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

        if (token is null)
        {
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

        // A socket with nothing to subscribe is closed by Twitch (4003); wait instead.
        if (!HasResolvedChannel())
        {
            await WaitForResolvedChannelAsync(ct);
            return null;
        }

        return new Session(this, token);
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

        schedule.SetPushStatus(PushStatus.FromCoverage(covered, watched.Length));
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
            HelixEventSubSubscription[] candidates = payload?.Data?
                .Where(subscription =>
                    string.Equals(subscription.Type, type, StringComparison.Ordinal)
                    && subscription.Condition is { } condition
                    && string.Equals(condition.BroadcasterUserId, userId, StringComparison.Ordinal))
                .ToArray() ?? [];

            bool matched = candidates.Any(subscription =>
                string.Equals(subscription.Status, "enabled", StringComparison.Ordinal)
                && subscription.Transport is { } transport
                && string.Equals(transport.Method, "websocket", StringComparison.Ordinal)
                && string.Equals(transport.SessionId, sessionId, StringComparison.Ordinal));

            if (!matched)
            {
                string existing = candidates.Length == 0
                    ? "none"
                    : string.Join(
                        ", ",
                        candidates.Select(subscription =>
                            $"{subscription.Id} ({subscription.Status}, " +
                            $"session {subscription.Transport?.SessionId ?? "unknown"})"));
                logger.LogWarning(
                    "EventSub 409 for {Type} for user {UserId} is not owned by session {SessionId}; existing: {Existing}",
                    type,
                    userId,
                    sessionId,
                    existing);
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

    private void Revoked(JsonElement payload)
    {
        string? revokedType = null;
        string? revokedStatus = null;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("subscription", out JsonElement subscription)
            && subscription.ValueKind == JsonValueKind.Object)
        {
            revokedType = ReadString(subscription, "type");
            revokedStatus = ReadString(subscription, "status");
        }

        schedule.SetPushStatus(PushStatus.Reconnecting);
        logger.LogWarning(
            "EventSub subscription {Type} was revoked ({Status}); reconnecting.",
            revokedType ?? "unknown",
            revokedStatus ?? "unknown");
        throw new EventSubRestartException("subscription revoked");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private enum SubscriptionResult
    {
        Active,
        Refused,
        Failed,
        Unauthorized,
        Capacity,
    }

    private sealed record SubscriptionAttempt(SubscriptionResult Result, string Token);

    private sealed class Session(WebSocketEventSubBinding owner, string token)
        : IEventSubSessionBinding
    {
        public Task<int> OnWelcomeAsync(string sessionId, CancellationToken ct) =>
            owner.SubscribeAllAsync(sessionId, token, ct);

        // Subscriptions move with the session, so the new socket needs nothing.
        public Task OnReconnectedAsync(string sessionId, CancellationToken ct) =>
            Task.CompletedTask;

        public void OnRevocation(JsonElement payload) => owner.Revoked(payload);
    }
}
