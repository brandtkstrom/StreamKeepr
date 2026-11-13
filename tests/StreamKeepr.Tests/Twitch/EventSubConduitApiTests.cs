using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class EventSubConduitApiTests : IDisposable
{
    private readonly FakeConduitTwitch _twitch = new();
    private readonly TwitchTokenProvider _tokens;
    private readonly EventSubConduitApi _api;

    public EventSubConduitApiTests()
    {
        var options = Options.Create(new TwitchOptions { ClientId = "client-id", ClientSecret = "secret" });
        var factory = new StubHttpClientFactory(_twitch);
        _tokens = new TwitchTokenProvider(
            factory, options, new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)));
        _api = new EventSubConduitApi(factory, _tokens, options, NullLogger<EventSubConduitApi>.Instance);
    }

    public void Dispose() => _tokens.Dispose();

    [Fact]
    public async Task Create_conduit_requests_one_shard_with_the_app_token()
    {
        string id = await _api.CreateConduitAsync(CancellationToken.None);

        Assert.Equal([id], _twitch.Conduits);
        FakeRequest create = Assert.Single(_twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/conduits"));
        Assert.Equal(1, create.Json!["shard_count"]!.GetValue<int>());
        Assert.Equal(_twitch.CurrentToken, create.Token);
    }

    [Fact]
    public async Task Create_conduit_at_the_limit_reports_it()
    {
        for (int i = 0; i < 5; i++)
            _twitch.AddConduit();

        var ex = await Assert.ThrowsAsync<TwitchApiException>(
            () => _api.CreateConduitAsync(CancellationToken.None));
        Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_conduits_lists_every_conduit_id()
    {
        string a = _twitch.AddConduit();
        string b = _twitch.AddConduit();

        Assert.Equal([a, b], await _api.GetConduitIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unauthorized_invalidates_the_app_token_and_retries_once()
    {
        int rejected = 0;
        _twitch.Override = request =>
            request.RequestUri!.AbsolutePath.EndsWith("conduits", StringComparison.Ordinal)
            && Interlocked.Increment(ref rejected) == 1
                ? FakeConduitTwitch.Error(HttpStatusCode.Unauthorized, "expired")
                : null;

        await _api.GetConduitIdsAsync(CancellationToken.None);

        FakeRequest[] gets = _twitch.RequestsMatching(HttpMethod.Get, "helix/eventsub/conduits");
        Assert.Equal(2, gets.Length);
        Assert.NotEqual(gets[0].Token, gets[1].Token);
    }

    [Fact]
    public async Task A_second_unauthorized_fails()
    {
        _twitch.Override = request =>
            request.RequestUri!.AbsolutePath.StartsWith("/helix", StringComparison.Ordinal)
                ? FakeConduitTwitch.Error(HttpStatusCode.Unauthorized, "bad client")
                : null;

        await Assert.ThrowsAsync<TwitchApiException>(() => _api.GetConduitIdsAsync(CancellationToken.None));
        Assert.Equal(2, _twitch.RequestsMatching(HttpMethod.Get, "helix/eventsub/conduits").Length);
    }

    [Fact]
    public async Task Delete_conduit_accepts_a_conduit_that_is_already_gone()
    {
        string id = _twitch.AddConduit();

        await _api.DeleteConduitAsync(id, CancellationToken.None);
        await _api.DeleteConduitAsync(id, CancellationToken.None);

        Assert.Empty(_twitch.Conduits);
    }

    [Fact]
    public async Task Assign_shard_binds_shard_zero_to_the_websocket_session()
    {
        string conduit = _twitch.AddConduit();

        await _api.AssignShardAsync(conduit, "session-1", CancellationToken.None);

        Assert.Equal("session-1", _twitch.ShardSessions[conduit]);
        FakeRequest patch = Assert.Single(
            _twitch.RequestsMatching(HttpMethod.Patch, "helix/eventsub/conduits/shards"));
        Assert.Equal("0", patch.Json!["shards"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("websocket", patch.Json!["shards"]![0]!["transport"]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Assign_shard_fails_when_twitch_reports_a_shard_error()
    {
        _twitch.Override = request => request.Method == HttpMethod.Patch
            ? new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    "{\"data\":[],\"errors\":[{\"id\":\"0\",\"message\":\"websocket session not found\",\"code\":\"invalid_parameter\"}]}"),
            }
            : null;

        var ex = await Assert.ThrowsAsync<TwitchApiException>(
            () => _api.AssignShardAsync("conduit-x", "session-1", CancellationToken.None));
        Assert.Contains("websocket session not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assign_shard_to_a_missing_conduit_fails()
    {
        await Assert.ThrowsAsync<TwitchApiException>(
            () => _api.AssignShardAsync("missing", "session-1", CancellationToken.None));
    }

    [Fact]
    public async Task List_subscriptions_follows_pagination_and_keeps_only_our_conduit()
    {
        string ours = _twitch.AddConduit();
        string other = _twitch.AddConduit();
        _twitch.Add("stream.online", "1", ours);
        _twitch.Add("stream.offline", "1", ours, status: "authorization_revoked");
        _twitch.Add("stream.online", "2", other);
        _twitch.Add("stream.online", "3", ours);
        _twitch.PageSize = 1;

        ConduitSubscriptionList list = await _api.ListSubscriptionsAsync(ours, CancellationToken.None);

        Assert.Equal(
            [
                new ConduitSubscription("sub-3", "enabled", "stream.online", "1"),
                new ConduitSubscription("sub-4", "authorization_revoked", "stream.offline", "1"),
                new ConduitSubscription("sub-6", "enabled", "stream.online", "3"),
            ],
            list.Subscriptions);
        Assert.Equal(10_000, list.MaxTotalCost);
        Assert.Equal(3, _twitch.RequestsMatching(HttpMethod.Get, "helix/eventsub/subscriptions").Length);
        Assert.All(
            _twitch.RequestsMatching(HttpMethod.Get, "helix/eventsub/subscriptions"),
            r => Assert.Equal(ours, r.Query["conduit_id"]));
    }

    [Fact]
    public async Task List_subscriptions_drops_entries_on_other_conduits_even_if_twitch_returns_them()
    {
        string ours = _twitch.AddConduit();
        _twitch.Add("stream.online", "1", ours);
        _twitch.Add("stream.online", "2", "someone-else");
        _twitch.Override = request => request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath.EndsWith("subscriptions", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":["
                        + "{\"id\":\"a\",\"status\":\"enabled\",\"type\":\"stream.online\",\"condition\":{\"broadcaster_user_id\":\"1\"},\"transport\":{\"method\":\"conduit\",\"conduit_id\":\"" + ours + "\"}},"
                        + "{\"id\":\"b\",\"status\":\"enabled\",\"type\":\"stream.online\",\"condition\":{\"broadcaster_user_id\":\"2\"},\"transport\":{\"method\":\"conduit\",\"conduit_id\":\"someone-else\"}},"
                        + "{\"id\":\"c\",\"status\":\"enabled\",\"type\":\"stream.online\",\"condition\":{\"broadcaster_user_id\":\"3\"},\"transport\":{\"method\":\"webhook\"}}"
                        + "],\"total_cost\":3,\"max_total_cost\":10000,\"pagination\":{}}"),
                }
                : null;

        ConduitSubscriptionList list = await _api.ListSubscriptionsAsync(ours, CancellationToken.None);

        Assert.Equal("a", Assert.Single(list.Subscriptions).Id);
    }

    [Fact]
    public async Task Create_subscription_uses_the_conduit_transport()
    {
        string conduit = _twitch.AddConduit();

        ConduitSubscribeOutcome outcome = await _api.CreateSubscriptionAsync(
            conduit, new ConduitSubscriptionKey("stream.online", "123"), CancellationToken.None);

        Assert.Equal(ConduitSubscribeOutcome.Created, outcome);
        FakeRequest create = Assert.Single(_twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions"));
        Assert.Equal("stream.online", create.Json!["type"]!.GetValue<string>());
        Assert.Equal("1", create.Json!["version"]!.GetValue<string>());
        Assert.Equal("123", create.Json!["condition"]!["broadcaster_user_id"]!.GetValue<string>());
        Assert.Equal("conduit", create.Json!["transport"]!["method"]!.GetValue<string>());
        Assert.Equal(conduit, create.Json!["transport"]!["conduit_id"]!.GetValue<string>());
        Assert.Null(create.Json!["transport"]!["session_id"]);
    }

    [Fact]
    public async Task Create_subscription_reports_conflict_and_refusal()
    {
        string conduit = _twitch.AddConduit();
        _twitch.Add("stream.online", "1", conduit);
        _twitch.RefusedUserIds.Add("2");

        Assert.Equal(
            ConduitSubscribeOutcome.AlreadyExists,
            await _api.CreateSubscriptionAsync(conduit, new("stream.online", "1"), CancellationToken.None));
        Assert.Equal(
            ConduitSubscribeOutcome.Refused,
            await _api.CreateSubscriptionAsync(conduit, new("stream.online", "2"), CancellationToken.None));
    }

    [Fact]
    public async Task Create_subscription_treats_429_as_refused_without_retrying()
    {
        _twitch.Override = request => request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("subscriptions", StringComparison.Ordinal)
                ? FakeConduitTwitch.Error(HttpStatusCode.TooManyRequests, "cost exceeded")
                : null;

        Assert.Equal(
            ConduitSubscribeOutcome.Refused,
            await _api.CreateSubscriptionAsync("c", new("stream.online", "1"), CancellationToken.None));
        Assert.Single(_twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions"));
    }

    [Fact]
    public async Task Create_subscription_server_error_throws()
    {
        _twitch.Override = request => request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("subscriptions", StringComparison.Ordinal)
                ? FakeConduitTwitch.Error(HttpStatusCode.ServiceUnavailable, "down")
                : null;

        await Assert.ThrowsAsync<TwitchApiException>(
            () => _api.CreateSubscriptionAsync("c", new("stream.online", "1"), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_subscription_accepts_one_that_is_already_gone()
    {
        string conduit = _twitch.AddConduit();
        FakeSubscription subscription = _twitch.Add("stream.online", "1", conduit);

        await _api.DeleteSubscriptionAsync(subscription.Id, CancellationToken.None);
        await _api.DeleteSubscriptionAsync(subscription.Id, CancellationToken.None);

        Assert.Empty(_twitch.Subscriptions);
    }
}
