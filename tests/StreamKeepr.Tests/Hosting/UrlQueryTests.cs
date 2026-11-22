using StreamKeepr.Hosting;

namespace StreamKeepr.Tests.Hosting;

public sealed class UrlQueryTests
{
    [Theory]
    [InlineData("/watch/a", "authError", "/watch/a")]
    [InlineData("/watch/a?authError=failed", "authError", "/watch/a")]
    [InlineData("/watch/a?t=10&AUTHERROR=failed&q=x", "authError", "/watch/a?t=10&q=x")]
    [InlineData("/watch/a?authError=one&authError=two&t=10", "authError", "/watch/a?t=10")]
    [InlineData("/watch/a?t=10", "authError", "/watch/a?t=10")]
    public void Without_removes_every_value_of_the_key_and_keeps_the_rest(string url, string key, string expected)
    {
        Assert.Equal(expected, UrlQuery.Without(url, key));
    }

    [Theory]
    [InlineData("https://id.example/authorize", "https://id.example/authorize?force_verify=true")]
    [InlineData("https://id.example/authorize?force_verify=false&state=s",
        "https://id.example/authorize?state=s&force_verify=true")]
    [InlineData("https://id.example/authorize?force_verify=true", "https://id.example/authorize?force_verify=true")]
    public void With_sets_exactly_one_value_last(string url, string expected)
    {
        Assert.Equal(expected, UrlQuery.With(url, "force_verify", "true"));
    }
}
