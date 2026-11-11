using System.Net;
using System.Text.RegularExpressions;

namespace StreamKeepr.Tests.Hosting;

public sealed class AppShellDocumentTests
{
    [Fact]
    public async Task CSS_loads_in_tokens_fonts_components_order_with_fingerprints()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        var tokens = html.IndexOf("css/tokens.", StringComparison.Ordinal);
        var fonts = html.IndexOf("css/fonts.", StringComparison.Ordinal);
        var components = html.IndexOf("css/components.", StringComparison.Ordinal);

        Assert.True(tokens >= 0, "tokens.css link is missing.");
        Assert.True(fonts > tokens, "fonts.css must follow tokens.css.");
        Assert.True(components > fonts, "components.css must follow fonts.css.");
        Assert.Matches(new Regex(@"css/tokens\.[a-z0-9]+\.css"), html);
        Assert.Matches(new Regex(@"css/fonts\.[a-z0-9]+\.css"), html);
        Assert.Matches(new Regex(@"css/components\.[a-z0-9]+\.css"), html);
        Assert.DoesNotContain("fonts.googleapis.com", html);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Document_shell_keeps_the_template_reconnect_modal_and_prefs_module()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("id=\"components-reconnect-modal\"", html);
        Assert.Contains("js/reconnect.", html);
        Assert.Contains("js/prefs.", html);
        Assert.Contains("_framework/blazor.web", html);
    }

    [Fact]
    public async Task Recording_route_renders_the_same_shell()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/r/abc123");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-unknown-recording", html);
        Assert.Contains("Back to library", html);
        Assert.Contains("class=\"sk-shell", html);
        Assert.Contains("data-slot=\"player\"", html);
        Assert.Contains("data-slot=\"stats\"", html);
        Assert.Contains("data-slot=\"details\"", html);
        Assert.True(
            html.IndexOf("data-slot=\"player\"", StringComparison.Ordinal) <
            html.IndexOf("data-slot=\"stats\"", StringComparison.Ordinal));
        Assert.True(
            html.IndexOf("data-slot=\"stats\"", StringComparison.Ordinal) <
            html.IndexOf("data-slot=\"details\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dev_component_gallery_is_not_reachable_outside_development()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dev/components");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Prerender_reads_the_drawer_mode_from_the_ui_cookie()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();
        var cookie =
            Uri.EscapeDataString(
                """{"v":1,"drawer":"rail","theater":false,"stats":false,"details":true,"collapsed":["pixelwren"]}""");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"sk-ui={cookie}");
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"sk-drawer rail\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
    }
}
