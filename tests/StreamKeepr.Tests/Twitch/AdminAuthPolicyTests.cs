using Microsoft.AspNetCore.Http;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class AdminAuthPolicyTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/settings?tab=twitch", "/settings?tab=twitch")]
    [InlineData("https://evil.example/steal", "/")]
    [InlineData("//evil.example/steal", "/")]
    [InlineData("/\\evil.example/steal", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("settings", "/")]
    [InlineData("~/settings", "/settings")]
    [InlineData("~//evil.example", "/")]
    [InlineData("/path\u0001with-control", "/")]
    public void Login_accepts_only_local_return_urls(string? returnUrl, string expected)
    {
        Assert.Equal(expected, AdminAuth.ResolveReturnUrl(returnUrl));
    }

    [Fact]
    public void Empty_allowlist_admits_any_login()
    {
        Assert.True(AdminAuth.IsLoginAllowed("someone", []));
    }

    [Fact]
    public void Configured_allowlist_rejects_other_logins()
    {
        Assert.False(AdminAuth.IsLoginAllowed("someone", ["allowed_login"]));
    }

    [Fact]
    public void Allowlist_comparison_is_case_insensitive()
    {
        Assert.True(AdminAuth.IsLoginAllowed("Allowed_Login", ["allowed_login"]));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("localhost:5080", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("streamkeepr.lan", false)]
    [InlineData("", false)]
    public void Loopback_hosts_allow_plain_http_sign_in(string host, bool expected)
    {
        Assert.Equal(expected, AdminAuth.IsLocalHost(new HostString(host)));
    }

    [Theory]
    [InlineData(null, "/?authError=failed")]
    [InlineData("/", "/?authError=failed")]
    [InlineData("/settings?tab=twitch", "/settings?tab=twitch&authError=failed")]
    [InlineData("/settings?authError=notAllowed&q=one", "/settings?q=one&authError=failed")]
    [InlineData("/settings?q=one#player", "/settings?q=one&authError=failed#player")]
    [InlineData("https://evil.example/steal", "/?authError=failed")]
    [InlineData("//evil.example/steal", "/?authError=failed")]
    public void Auth_error_redirects_keep_the_local_path_and_replace_the_reason(
        string? returnUrl, string expected)
    {
        Assert.Equal(expected, AdminAuth.AuthErrorRedirectUrl(returnUrl, AdminAuth.FailedAuthError));
    }
}
