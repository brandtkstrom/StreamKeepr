using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class LiveIndexEndpointTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recording_session_returns_a_live_stitch_without_endlist()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "123456789");
        await WritePartAsync(session, "p001_00001.m4s");

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.apple.mpegurl",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_session_returns_the_finalized_index_with_one_endlist()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "223456789");
        await WritePartAsync(session, "p001_00001.m4s");
        await factory.Services.GetRequiredService<SessionStore>()
            .FinalizeAsync(session, CancellationToken.None);

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.apple.mpegurl",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(body, "#EXT-X-ENDLIST"));
    }

    [Fact]
    public async Task Unknown_channel_or_session_returns_404()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "323456789");
        await WritePartAsync(session, "p001_00001.m4s");

        using var unknownSession = await client.GetAsync(
            $"/media/{session.Channel}/20260101T000000Z_999999999/index.m3u8");
        using var unknownChannel = await client.GetAsync(
            $"/media/missing/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, unknownSession.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownChannel.StatusCode);
        Assert.Equal("no-cache", unknownSession.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", unknownChannel.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Invalid_channel_or_session_id_returns_404()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "423456789");
        await WritePartAsync(session, "p001_00001.m4s");

        using var uppercaseChannel = await client.GetAsync(
            $"/media/Streamer/{session.Id}/index.m3u8");
        using var badId = await client.GetAsync(
            $"/media/{session.Channel}/not-a-session/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, uppercaseChannel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, badId.StatusCode);
        Assert.Equal("no-cache", uppercaseChannel.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", badId.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Corrupt_manifest_returns_404_instead_of_500()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "523456789");
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "session.json"), "{ not json");

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    private static async Task<Session> OpenSessionAsync(
        StreamKeeprAppFactory factory, string streamId)
    {
        var live = new LiveStream(streamId, "Title", "Game", StartedAt, null);
        var session = Session.For(live, "streamer", factory.RecordingsRoot);
        await factory.Services.GetRequiredService<SessionStore>()
            .OpenAsync(session, live, CancellationToken.None);
        return session;
    }

    private static Task WritePartAsync(Session session, string segment)
    {
        Directory.CreateDirectory(session.Directory);
        return File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                6,
                "init_001.mp4",
                PlaylistTestData.Segment(segment, "2026-11-10T16:35:00Z", 6)));
    }
}
