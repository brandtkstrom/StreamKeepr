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
