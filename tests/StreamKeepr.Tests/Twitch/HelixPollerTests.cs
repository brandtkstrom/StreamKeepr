using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class HelixPollerTests
{
    [Fact]
    public async Task Observe_maps_live_and_offline_channels()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : Streams("""
                    {"data":[
                      {"id":"123","user_login":"live","type":"live","title":"Title",
                       "game_name":"Game","started_at":"2026-11-10T16:30:00Z",
                       "thumbnail_url":"https://thumb/{width}x{height}"}
                    ]}
                    """)));
        TwitchStreamPoller poller = CreatePoller(handler);

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(["live", "offline"], CancellationToken.None);

        var live = Assert.IsType<StreamObservation.Live>(observations["live"]);
        Assert.Equal("123", live.Stream.StreamId);
        Assert.Equal("Title", live.Stream.Title);
        Assert.Equal("Game", live.Stream.Game);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero),
            live.Stream.StartedAt);
        Assert.Equal("https://thumb/{width}x{height}", live.Stream.ThumbnailUrl);
        Assert.IsType<StreamObservation.Offline>(observations["offline"]);
    }

    [Theory]
    [InlineData("abc", "live")]
    [InlineData("456", "rerun")]
    public async Task Non_live_or_non_numeric_streams_are_offline(
        string id, string type)
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : Streams($$"""
                    {"data":[
                      {"id":"{{id}}","user_login":"streamer","type":"{{type}}",
                       "title":"Title","game_name":"Game",
                       "started_at":"2026-11-10T16:30:00Z"}
                    ]}
                    """)));
        TwitchStreamPoller poller = CreatePoller(handler);

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(["streamer"], CancellationToken.None);

        Assert.IsType<StreamObservation.Offline>(observations["streamer"]);
    }

    [Fact]
    public async Task A_batch_failure_is_unknown_for_every_login()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        TwitchStreamPoller poller = CreatePoller(handler);

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(["one", "two"], CancellationToken.None);

        Assert.All(observations.Values, observation =>
            Assert.IsType<StreamObservation.Unknown>(observation));
    }

    [Fact]
    public async Task Streams_are_requested_in_batches_of_one_hundred()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : Streams("""{"data":[]}""")));
        TwitchStreamPoller poller = CreatePoller(handler);
        string[] logins = Enumerable.Range(0, 150)
            .Select(index => $"channel_{index}")
            .ToArray();

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(logins, CancellationToken.None);

        Assert.Equal(150, observations.Count);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.RequestUri!.AbsolutePath.Contains("/helix/streams", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unauthorized_refreshes_the_token_and_retries_once()
    {
        int streamAttempts = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Task.FromResult(Token());

            streamAttempts++;
            return Task.FromResult(streamAttempts == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Streams("""{"data":[]}"""));
        });
        TwitchStreamPoller poller = CreatePoller(handler);

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(["streamer"], CancellationToken.None);

        Assert.IsType<StreamObservation.Offline>(observations["streamer"]);
        Assert.Equal(2, streamAttempts);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.RequestUri!.Host == "id.twitch.tv"));
    }

    [Fact]
    public async Task A_second_unauthorized_becomes_unknown()
    {
        int streamAttempts = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Task.FromResult(Token());

            streamAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        TwitchStreamPoller poller = CreatePoller(handler);

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(["streamer"], CancellationToken.None);

        Assert.IsType<StreamObservation.Unknown>(observations["streamer"]);
        Assert.Equal(2, streamAttempts);
    }

    [Fact]
    public async Task User_lookup_returns_the_profile()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : Json("""
                    {"data":[
                      {"id":"123456","login":"streamer","display_name":"Streamer",
                       "profile_image_url":"https://avatar"}
                    ]}
                    """)));
        TwitchStreamPoller poller = CreatePoller(handler);

        ChannelProfile? profile = await poller.GetUserAsync(
            " Streamer ", CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("123456", profile!.UserId);
        Assert.Equal("Streamer", profile.DisplayName);
        Assert.Equal("https://avatar", profile.AvatarUrl);
        Assert.Contains(handler.Requests, request =>
            request.RequestUri!.Host == "api.twitch.tv"
            && request.Headers.TryGetValues("Client-Id", out IEnumerable<string>? values)
            && values.Single() == "client-id");
    }

    [Fact]
    public async Task Server_errors_are_retried_once_before_becoming_unknown()
    {
        int streamAttempts = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Task.FromResult(Token());

            streamAttempts++;
            return Task.FromResult(streamAttempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Streams("""{"data":[]}"""));
        });
        TwitchStreamPoller poller = CreatePoller(handler);

        IReadOnlyDictionary<string, StreamObservation> observations =
            await poller.ObserveAsync(["streamer"], CancellationToken.None);

        Assert.IsType<StreamObservation.Offline>(observations["streamer"]);
        Assert.Equal(2, streamAttempts);
    }

    [Fact]
    public async Task User_lookup_returns_null_when_twitch_has_no_user()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : Json("""{"data":[]}""")));
        TwitchStreamPoller poller = CreatePoller(handler);

        Assert.Null(await poller.GetUserAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task User_lookup_surfaces_a_twitch_outage()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        TwitchStreamPoller poller = CreatePoller(handler);

        await Assert.ThrowsAsync<TwitchApiException>(
            () => poller.GetUserAsync("streamer", CancellationToken.None));
    }

    [Fact]
    public async Task Channel_info_returns_title_and_game()
    {
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? Token()
                : Json("""
                    {"data":[{"title":"Current title","game_name":"Current game"}]}
                    """)));
        TwitchStreamPoller poller = CreatePoller(handler);

        ChannelInfo? info = await poller.GetChannelInfoAsync("123456", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("Current title", info!.Title);
        Assert.Equal("Current game", info.Game);
    }

    [Fact]
    public async Task A_recording_channel_forces_the_short_interval_even_with_full_push_coverage()
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var status = new DvrStatus();
        var session = new Session(
            "20261110T163000Z_1", "streamer", "1", clock.GetUtcNow(), "/tmp/session");
        status.Applied(
            "streamer",
            null,
            new DvrState.Recording(
                session,
                new JobId(session.Id, 1),
                new LiveStream("1", "Title", "Game", clock.GetUtcNow(), null),
                Attempt: 0,
                HasMedia: false,
                Streak: 0),
            clock.GetUtcNow());

        var schedule = new PollSchedule(clock);
        schedule.SetPushCoverage(true);

        int polls = 0;
        var firstPoll = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPoll = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Task.FromResult(Token());

            int poll = Interlocked.Increment(ref polls);
            if (poll == 1)
                firstPoll.TrySetResult();
            else if (poll == 2)
                secondPoll.TrySetResult();

            return Task.FromResult(Streams("""{"data":[]}"""));
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            PollIntervalSeconds = 30,
            PollIntervalWithEventSubSeconds = 300,
        });
        var poller = new TwitchStreamPoller(
            factory,
            new TwitchTokenProvider(factory, options, clock),
            schedule,
            status,
            options,
            NullLogger<TwitchStreamPoller>.Instance);

        using var stop = new CancellationTokenSource();
        Task run = poller.RunAsync(
            () => ["streamer"],
            (_, _, _) => ValueTask.CompletedTask,
            stop.Token);

        await firstPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, Volatile.Read(ref polls));

        clock.Advance(TimeSpan.FromSeconds(1));
        await secondPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Nudge_wakes_the_run_loop_for_an_immediate_poll()
    {
        int polls = 0;
        var firstPoll = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPoll = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Task.FromResult(Token());

            int poll = Interlocked.Increment(ref polls);
            if (poll == 1)
                firstPoll.TrySetResult();
            else if (poll == 2)
                secondPoll.TrySetResult();

            return Task.FromResult(Streams("""{"data":[]}"""));
        });
        TwitchStreamPoller poller = CreatePoller(handler);

        using var stop = new CancellationTokenSource();
        Task run = poller.RunAsync(
            () => ["streamer"],
            (_, _, _) => ValueTask.CompletedTask,
            stop.Token);

        await firstPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        poller.Nudge();
        await secondPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TwitchStreamPoller CreatePoller(StubHandler handler)
    {
        var factory = new StubHttpClientFactory(handler);
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            PollIntervalSeconds = 60,
        });
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));

        return new TwitchStreamPoller(
            factory,
            new TwitchTokenProvider(factory, options, clock),
            new PollSchedule(clock),
            new DvrStatus(),
            options,
            NullLogger<TwitchStreamPoller>.Instance);
    }

    private static HttpResponseMessage Token() =>
        Json("""{"access_token":"token","expires_in":3600}""");

    private static HttpResponseMessage Streams(string body) => Json(body);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
