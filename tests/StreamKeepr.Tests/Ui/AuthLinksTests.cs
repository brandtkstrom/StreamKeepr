using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class AuthLinksTests
{
    [Theory]
    [InlineData("http://host/", "/")]
    [InlineData("http://host/r/abc?q=chef&sort=az", "/r/abc?q=chef&sort=az")]
    [InlineData("http://host/r/abc?authError=failed&q=chef#top", "/r/abc?q=chef")]
    [InlineData("http://host/settings?AUTHERROR=notAllowed", "/settings")]
    public void The_return_url_is_the_local_path_and_query_without_auth_errors(string uri, string expected)
    {
        Assert.Equal(expected, AuthLinks.ReturnUrl(uri));
    }

    [Fact]
    public void The_login_link_carries_the_escaped_return_url()
    {
        Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fr%2Fabc%3Fq%3Dchef%2520night",
            AuthLinks.LoginHref("http://host/r/abc?q=chef%20night&authError=failed"));
    }

    [Theory]
    [InlineData("notAllowed", "This Twitch account is not allowed to manage Stream Keepr.")]
    [InlineData("httpsRequired", "Twitch sign-in requires HTTPS except on localhost.")]
    [InlineData("failed", "Twitch sign-in failed. Try again.")]
    public void Known_auth_errors_have_one_message(string code, string message)
    {
        Assert.Equal(message, AuthLinks.AuthErrorMessage($"http://host/?authError={code}"));
    }

    [Theory]
    [InlineData("http://host/")]
    [InlineData("http://host/?authError=")]
    [InlineData("http://host/?authError=somethingElse")]
    public void Missing_or_unknown_codes_have_no_message(string uri)
    {
        Assert.Null(AuthLinks.AuthErrorMessage(uri));
    }

    [Fact]
    public void Removing_the_auth_error_keeps_the_path_and_other_keys()
    {
        Assert.Equal(
            "/r/abc?q=chef&state=saved&sort=az",
            AuthLinks.WithoutAuthError("http://host/r/abc?q=chef&authError=failed&state=saved&sort=az"));
        Assert.Equal("/", AuthLinks.WithoutAuthError("http://host/?authError=failed"));
    }
}
