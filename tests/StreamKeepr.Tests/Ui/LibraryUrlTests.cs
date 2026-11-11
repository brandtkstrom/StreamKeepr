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
    public void Round_trips()
    {
        var query = new LibraryQuery("recording", "a&b=c", "az");

        Assert.Equal(query, LibraryUrl.Parse("http://host" + LibraryUrl.With("http://host/", query)));
    }
}
