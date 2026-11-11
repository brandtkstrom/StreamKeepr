using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

public sealed class DomainModelTests
{
    [Fact]
    public void Session_id_uses_utc_start_time_and_stream_id_and_keeps_titles_out_of_paths()
    {
        var live = new LiveStream(
            StreamId: "123456789",
            Title: "Title/with:unsafe?characters",
            Game: "Just Chatting",
            StartedAt: new DateTimeOffset(2026, 11, 10, 18, 30, 0, TimeSpan.FromHours(2)),
            ThumbnailUrl: null);

        var session = Session.For(live, "streamer", "/recordings");

        Assert.Equal("20261110T163000Z_123456789", session.Id);
        Assert.Equal("streamer", session.Channel);
        Assert.Equal("123456789", session.StreamId);
        Assert.Equal(live.StartedAt, session.StartedAt);
        Assert.Equal(
            Path.Combine("/recordings", "streamer", "20261110T163000Z_123456789"),
            session.Directory);
        Assert.DoesNotContain(live.Title, session.Id, StringComparison.Ordinal);
        Assert.DoesNotContain(live.Title, session.Directory, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_for_rejects_a_stream_id_that_cannot_form_a_safe_path()
    {
        var live = new LiveStream(
            StreamId: "../not-a-stream-id",
            Title: "Title",
            Game: "Game",
            StartedAt: new DateTimeOffset(2026, 11, 10, 18, 30, 0, TimeSpan.Zero),
            ThumbnailUrl: null);

        Assert.Throws<ArgumentException>(() => Session.For(live, "streamer", "/recordings"));
    }

    [Fact]
    public void Session_ids_accept_only_the_derived_shape()
    {
        Assert.True(Session.IsValidId("20261110T163000Z_123456789"));
        Assert.False(Session.IsValidId("20261110T163000Z_"));
        Assert.False(Session.IsValidId("not-a-session"));
        Assert.False(Session.IsValidId("../20261110T163000Z_123456789"));
    }

    [Fact]
    public void Job_id_is_deterministic_for_a_session_and_zero_padded_part()
    {
        var job = new JobId("20261110T163000Z_123456789", 2);

        Assert.Equal("20261110T163000Z_123456789/p002", job.ToString());
    }
}
