using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>The subscriptions on one conduit, plus the client's cost budget as Twitch reports it.</summary>
public sealed record ConduitSubscriptionList(
    IReadOnlyList<ConduitSubscription> Subscriptions, int TotalCost, int MaxTotalCost);

public enum ConduitSubscribeOutcome
{
    /// <summary>Twitch accepted the subscription (202).</summary>
    Created,

    /// <summary>A matching subscription already exists (409). The caller verifies it is on our conduit.</summary>
    AlreadyExists,

    /// <summary>Twitch refused it (4xx, including a cost or rate 429); the channel stays polling-only.</summary>
    Refused,
}

/// <summary>
/// Helix conduit, shard, and EventSub subscription calls with the app access token. A 401
/// invalidates the token and retries once (contract §2). Failures throw
/// <see cref="TwitchApiException"/>; the token and client secret are never logged.
/// </summary>
public sealed class EventSubConduitApi(
    IHttpClientFactory httpFactory,
    TwitchTokenProvider tokens,
    IOptions<TwitchOptions> options,
    ILogger<EventSubConduitApi> logger)
{
    private const string ConduitsPath = "helix/eventsub/conduits";
    private const string ShardsPath = "helix/eventsub/conduits/shards";
    private const string SubscriptionsPath = "helix/eventsub/subscriptions";

    // Shard IDs start at 0; the first slice runs one WebSocket shard.
    private const string ShardId = "0";

    public async Task<IReadOnlyList<string>> GetConduitIdsAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, ConduitsPath), ct);
        await EnsureSuccessAsync(response, "list EventSub conduits", ct);

        HelixConduitsResponse? body = await response.Content.ReadFromJsonAsync(
            TwitchJsonContext.Default.HelixConduitsResponse, ct);
        return body?.Data?.Select(conduit => conduit.Id).ToArray() ?? [];
    }

    public async Task<string> CreateConduitAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ConduitsPath)
            {
                Content = JsonContent.Create(
                    new CreateConduitRequest(ShardCount: 1),
                    TwitchJsonContext.Default.CreateConduitRequest),
            },
            ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new TwitchApiException(
                "EventSub conduit limit reached: this client ID already has the maximum number of "
                + "conduits. Unused conduits expire 72 hours after their last shard disconnects. "
                + await ReadMessageAsync(response, ct));
        }

        await EnsureSuccessAsync(response, "create an EventSub conduit", ct);
        HelixConduitsResponse? body = await response.Content.ReadFromJsonAsync(
            TwitchJsonContext.Default.HelixConduitsResponse, ct);
        return body?.Data?.FirstOrDefault()?.Id is { Length: > 0 } id
            ? id
            : throw new TwitchApiException("Twitch returned no conduit ID.");
    }

    /// <summary>Deletes a conduit; one that no longer exists counts as deleted.</summary>
    public async Task DeleteConduitAsync(string conduitId, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Delete, $"{ConduitsPath}?id={Uri.EscapeDataString(conduitId)}"),
            ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete the EventSub conduit", ct);
    }

    /// <summary>
    /// Binds shard 0 to a WebSocket session. Twitch closes the socket unless this happens
    /// within 10 seconds of its <c>session_welcome</c>.
    /// </summary>
    public async Task AssignShardAsync(string conduitId, string sessionId, CancellationToken ct)
    {
        var body = new UpdateConduitShardsRequest(
            conduitId,
            [new ConduitShardUpdate(ShardId, new EventSubTransport("websocket", SessionId: sessionId))]);
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, ShardsPath)
            {
                Content = JsonContent.Create(body, TwitchJsonContext.Default.UpdateConduitShardsRequest),
            },
            ct);
        await EnsureSuccessAsync(response, "assign the EventSub conduit shard", ct);

        // A 202 can still carry per-shard errors.
        UpdateConduitShardsResponse? result = await response.Content.ReadFromJsonAsync(
            TwitchJsonContext.Default.UpdateConduitShardsResponse, ct);
        if (result?.Errors is [ConduitShardError error, ..])
        {
            throw new TwitchApiException(
                $"Twitch refused the conduit shard assignment: {error.Code} {error.Message}".TrimEnd());
        }
    }

    /// <summary>
    /// Lists every subscription on the conduit across pages. Entries whose transport names a
    /// different conduit are dropped even if Twitch returns them.
    /// </summary>
    public async Task<ConduitSubscriptionList> ListSubscriptionsAsync(string conduitId, CancellationToken ct)
    {
        var subscriptions = new List<ConduitSubscription>();
        string? cursor = null;
        int totalCost = 0;
        int maxTotalCost = 0;

        do
        {
            string uri = $"{SubscriptionsPath}?conduit_id={Uri.EscapeDataString(conduitId)}"
                + (cursor is null ? "" : $"&after={Uri.EscapeDataString(cursor)}");
            using HttpResponseMessage response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
            await EnsureSuccessAsync(response, "list EventSub subscriptions", ct);

            HelixEventSubSubscriptionsResponse? page = await response.Content.ReadFromJsonAsync(
                TwitchJsonContext.Default.HelixEventSubSubscriptionsResponse, ct);
            subscriptions.AddRange(
                (page?.Data ?? [])
                    .Where(s => s.Transport?.ConduitId == conduitId)
                    .Select(s => new ConduitSubscription(
                        s.Id, s.Status, s.Type, s.Condition?.BroadcasterUserId)));
            totalCost = page?.TotalCost ?? totalCost;
            maxTotalCost = page?.MaxTotalCost ?? maxTotalCost;
            cursor = page?.Pagination?.Cursor is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null);

        return new ConduitSubscriptionList(subscriptions, totalCost, maxTotalCost);
    }

    public async Task<ConduitSubscribeOutcome> CreateSubscriptionAsync(
        string conduitId, ConduitSubscriptionKey key, CancellationToken ct)
    {
        var body = new EventSubSubscriptionRequest(
            key.Type,
            "1",
            new EventSubCondition(key.BroadcasterUserId),
            new EventSubTransport("conduit", ConduitId: conduitId));
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, SubscriptionsPath)
            {
                Content = JsonContent.Create(body, TwitchJsonContext.Default.EventSubSubscriptionRequest),
            },
            ct);

        if (response.IsSuccessStatusCode)
            return ConduitSubscribeOutcome.Created;
        if (response.StatusCode == HttpStatusCode.Conflict)
            return ConduitSubscribeOutcome.AlreadyExists;

        int status = (int)response.StatusCode;
        if (status is >= 400 and < 500 && response.StatusCode != HttpStatusCode.Unauthorized)
        {
            // Includes 429: the cost budget, the per-condition cap, or the rate bucket. Polling
            // covers the channel until the next session reconciles again.
            logger.LogWarning(
                "Twitch refused EventSub {Type} for broadcaster {BroadcasterId} with HTTP {Status}: {Message}",
                key.Type,
                key.BroadcasterUserId,
                status,
                await ReadMessageAsync(response, ct));
            return ConduitSubscribeOutcome.Refused;
        }

        await EnsureSuccessAsync(response, $"create EventSub {key.Type}", ct);
        return ConduitSubscribeOutcome.Created;
    }

    /// <summary>Deletes a subscription; one that no longer exists counts as deleted.</summary>
    public async Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Delete, $"{SubscriptionsPath}?id={Uri.EscapeDataString(subscriptionId)}"),
            ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete an EventSub subscription", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Helix);
        for (int attempt = 0; ; attempt++)
        {
            string token = await tokens.GetAsync(ct);
            using HttpRequestMessage request = createRequest();
            request.Headers.Add("Client-Id", options.Value.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                return response;

            response.Dispose();
            tokens.Invalidate(token);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new TwitchApiException(
            $"Could not {action}: HTTP {(int)response.StatusCode}. {await ReadMessageAsync(response, ct)}"
                .TrimEnd());
    }

    /// <summary>Twitch's error message, if the body has one. It never contains credentials.</summary>
    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            HelixErrorResponse? error = await response.Content.ReadFromJsonAsync(
                TwitchJsonContext.Default.HelixErrorResponse, ct);
            return error?.Message ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
