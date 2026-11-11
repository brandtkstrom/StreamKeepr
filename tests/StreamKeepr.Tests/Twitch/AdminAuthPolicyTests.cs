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
}
