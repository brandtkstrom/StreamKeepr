using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Mock;

namespace StreamKeepr.Tests.Mock;

public sealed class MockAuthTests
{
    [Fact]
    public async Task Signed_in_by_default_sets_the_admin_cookie()
    {
        using var factory = new MockAppFactory(signedIn: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Contains(
            SetCookies(response),
            cookie => cookie.StartsWith("sk-auth=", StringComparison.Ordinal));

        Assert.IsType<MockStreamPoller>(factory.Services.GetRequiredService<IStreamPoller>());
        Assert.IsType<MockEventSubClient>(factory.Services.GetRequiredService<IEventSubClient>());
        Assert.Null(factory.Services.GetService<TwitchTokenProvider>());
    }

    [Fact]
    public async Task Signed_out_toggle_omits_the_admin_cookie()
    {
        using var factory = new MockAppFactory(signedIn: false);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(
            SetCookies(response),
            cookie => cookie.StartsWith("sk-auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fake_logout_needs_antiforgery_then_stays_signed_out_until_fake_login()
    {
        using var factory = new MockAppFactory(signedIn: true);
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string page = await client.GetStringAsync("/r/abc?q=chef");
        Assert.Contains("data-auth-state=\"connected\"", page, StringComparison.Ordinal);

        using (HttpResponseMessage forged = await client.PostAsync(
            "/api/auth/logout",
            new FormUrlEncodedContent([new("returnUrl", "/")])))
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, forged.StatusCode);
        }

        string token = System.Text.RegularExpressions.Regex.Match(
            page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        using (HttpResponseMessage logout = await client.PostAsync(
            "/api/auth/logout",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("returnUrl", "/r/abc?q=chef")])))
        {
            Assert.Equal(System.Net.HttpStatusCode.Redirect, logout.StatusCode);
            Assert.Equal("/r/abc?q=chef", logout.Headers.Location?.OriginalString);
        }

        string signedOut = await client.GetStringAsync("/r/abc?q=chef");
        Assert.Contains("data-auth-state=\"disconnected\"", signedOut, StringComparison.Ordinal);
        Assert.Contains("/api/auth/twitch/login?returnUrl=%2Fr%2Fabc%3Fq%3Dchef", signedOut, StringComparison.Ordinal);

        using (HttpResponseMessage login = await client.GetAsync("/api/auth/twitch/login?returnUrl=%2Fr%2Fabc%3Fq%3Dchef"))
        {
            Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal("/r/abc?q=chef", login.Headers.Location?.OriginalString);
        }

        string signedIn = await client.GetStringAsync("/");
        Assert.Contains("data-auth-state=\"connected\"", signedIn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_login_rejects_external_return_urls()
    {
        using var factory = new MockAppFactory(signedIn: false);
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage login = await client.GetAsync(
            "/api/auth/twitch/login?returnUrl=https%3A%2F%2Fevil.example%2F");

        Assert.Equal("/", login.Headers.Location?.OriginalString);
        Assert.Contains("data-auth-state=\"connected\"", await client.GetStringAsync("/"), StringComparison.Ordinal);
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values
            : [];

    private sealed class MockAppFactory(bool signedIn) : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(MockEnvironment.Name);
            builder.ConfigureTestServices(services =>
            {
                // The Mock host forces its own temp root and seed defaults while Program
                // builds; these overrides run after those registrations.
                services.PostConfigure<StorageOptions>(options => options.RecordingsRoot = _root);
                services.PostConfigure<MockSeedOptions>(options => options.Enabled = false);
                services.PostConfigure<MockAuthOptions>(options => options.SignedIn = signedIn);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
