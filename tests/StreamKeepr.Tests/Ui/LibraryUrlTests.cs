using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class LibraryUrlTests
{
    [Fact]
    public void Missing_keys_mean_no_search_no_filter_newest()
    {
        Assert.Equal(new LibraryQuery(null, null, "newest"), LibraryUrl.Parse("http://host/"));
        Assert.Equal(new LibraryQuery(null, null, "newest"), LibraryUrl.Parse("http://host/r/abc"));
    }

    [Fact]
    public void Reads_q_state_and_sort()
    {
        LibraryQuery query = LibraryUrl.Parse(
            "http://host/r/abc?q=%20Chef%20Ann%20&state=Recording&sort=az");

        Assert.Equal(new LibraryQuery("recording", "Chef Ann", "az"), query);
        Assert.Equal("saved", LibraryUrl.Parse("http://host/?state=saved").State);
        Assert.Equal("oldest", LibraryUrl.Parse("http://host/?sort=oldest").Sort);
    }

    [Theory]
    [InlineData("http://host/?state=partial")]
    [InlineData("http://host/?state=reconnecting")]
    [InlineData("http://host/?state=")]
    [InlineData("http://host/?sort=sideways&q=%20%20")]
    public void Invalid_or_empty_values_fall_back(string uri)
    {
        Assert.Equal(new LibraryQuery(null, null, "newest"), LibraryUrl.Parse(uri));
    }

    [Fact]
    public void With_writes_only_non_default_keys_and_keeps_the_path_and_other_keys()
    {
        Assert.Equal(
            "/r/abc?authError=x&q=chef%20ann&state=saved&sort=oldest",
            LibraryUrl.With(
                "http://host/r/abc?authError=x&q=old",
                new LibraryQuery("saved", "chef ann", "oldest")));

        Assert.Equal(
            "/",
            LibraryUrl.With("http://host/?q=old&state=saved&sort=az", new LibraryQuery()));
    }

    [Fact]
    public void Autoplay_is_a_one_shot_player_intent()
    {
        Assert.False(LibraryUrl.AutoplayRequested("http://host/r/abc"));
        Assert.False(LibraryUrl.AutoplayRequested("http://host/r/abc?autoplay=0"));
        Assert.True(LibraryUrl.AutoplayRequested("http://host/r/abc?autoplay=1"));

        Assert.Equal("/r/abc?autoplay=1", LibraryUrl.WithAutoplay("/r/abc"));
        Assert.Equal(
            "/r/abc?q=chef%20ann&autoplay=1",
            LibraryUrl.WithAutoplay("/r/abc?q=chef%20ann"));
        Assert.Equal(
            "/r/abc?q=chef%20ann&autoplay=1",
            LibraryUrl.WithAutoplay("/r/abc?autoplay=1&q=chef%20ann&autoplay=1"));

        Assert.Equal(
            "/r/abc?q=chef%20ann",
            LibraryUrl.WithoutAutoplay("http://host/r/abc?q=chef%20ann&autoplay=1"));
        Assert.Equal("/r/abc", LibraryUrl.WithoutAutoplay("http://host/r/abc?autoplay=1"));
    }

    [Fact]
    public void With_drops_the_autoplay_intent_so_next_navigation_does_not_inherit_it()
    {
        Assert.Equal(
            "/r/abc?q=chef",
            LibraryUrl.With(
                "http://host/r/abc?autoplay=1&q=old",
                new LibraryQuery(null, "chef", "newest")));
    }

    [Fact]
    public void Autoplay_preserves_an_already_escaped_path()
    {
        Assert.Equal("/r/a%20b?autoplay=1", LibraryUrl.WithAutoplay("/r/a%20b"));
        Assert.Equal(
            "/r/a%20b",
            LibraryUrl.WithoutAutoplay("http://host/r/a%20b?autoplay=1"));
    }

    [Fact]
    public void Round_trips()
    {
        var query = new LibraryQuery("recording", "a&b=c", "az");

        Assert.Equal(query, LibraryUrl.Parse("http://host" + LibraryUrl.With("http://host/", query)));
    }

    [Theory]
    [InlineData("http://localhost/r/abc", "abc")]
    [InlineData("http://localhost/r/abc?q=chef#t", "abc")]
    [InlineData("http://localhost/r/a%20b", "a b")]
    [InlineData("http://localhost/", null)]
    [InlineData("http://localhost/r/", null)]
    [InlineData("http://localhost/r/abc/extra", null)]
    [InlineData("http://localhost/settings", null)]
    [InlineData("http://localhost/rr/abc", null)]
    public void RecordingId_reads_the_watch_page_route(string uri, string? expected)
    {
        Assert.Equal(expected, LibraryUrl.RecordingId(uri));
    }
}
