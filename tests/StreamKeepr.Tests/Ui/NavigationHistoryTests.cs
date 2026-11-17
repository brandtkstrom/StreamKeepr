using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class NavigationHistoryTests
{
    [Fact]
    public void A_direct_visit_has_no_previous_page()
    {
        var history = new NavigationHistory("http://localhost/settings");

        Assert.False(history.HasPreviousPage);
    }

    [Fact]
    public void Arriving_from_another_page_has_a_previous_page()
    {
        var history = new NavigationHistory("http://localhost/r/abc");

        history.Visit("http://localhost/settings");

        Assert.True(history.HasPreviousPage);
    }

    [Theory]
    [InlineData("http://localhost/settings?authError=failed", "http://localhost/settings")]
    [InlineData("http://localhost/settings", "http://localhost/settings#channels")]
    public void Changing_only_the_query_or_fragment_is_the_same_page(string start, string next)
    {
        // A replaced query (the authError notifier) or an in-page anchor adds no page to go back to.
        var history = new NavigationHistory(start);

        history.Visit(next);

        Assert.False(history.HasPreviousPage);
    }

    [Fact]
    public void A_previous_page_stays_known_after_more_visits()
    {
        var history = new NavigationHistory("http://localhost/");

        history.Visit("http://localhost/settings");
        history.Visit("http://localhost/settings?x=1");

        Assert.True(history.HasPreviousPage);
    }
}
