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
        // No asset loads from another origin. The app bar's GitHub and Twitch links are
        // outbound anchors (#174), so the check is on what the page loads, not on every URL.
        Assert.DoesNotMatch(new Regex(@"<link\b[^>]*\bhref=""(?:https?:)?//", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\bsrc=""(?:https?:)?//", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\bsrcset=""[^""]*(?:https?:)?//", RegexOptions.IgnoreCase), html);
    }

    [Fact]
    public async Task The_prerendered_shell_already_has_its_real_links()
    {
        // Before the circuit connects (and before MudBlazor knows the breakpoint) the page is this
        // HTML, so every bar destination must already be a real link (#174).
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        var bottomNav = Regex.Match(html, @"<nav[^>]*aria-label=""Main""[^>]*>(?<body>.*?)</nav>", RegexOptions.Singleline);
        Assert.True(bottomNav.Success, "the bottom bar is missing from the prerendered page");
        Assert.Contains(@"href=""/""", bottomNav.Groups["body"].Value);
        Assert.Contains(@"href=""/settings""", bottomNav.Groups["body"].Value);
        Assert.Matches(new Regex(@"<a(?=[^>]*href=""https://github\.com/)(?=[^>]*target=""_blank"")"), html);
        Assert.Matches(new Regex(@"<a(?=[^>]*href=""/settings"")(?=[^>]*aria-label=""Settings"")"), html);
    }

    [Fact]
    public async Task MudBlazor_css_loads_before_the_app_css_and_its_script_after_blazor()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");
        var mudCss = Regex.Match(html, @"href=""(?<href>_content/MudBlazor/MudBlazor\.min(?:\.[a-z0-9]+)?\.css)""");
        var mudJs = Regex.Match(html, @"src=""(?<src>_content/MudBlazor/MudBlazor\.min(?:\.[a-z0-9]+)?\.js)""");

        Assert.True(mudCss.Success, "MudBlazor.min.css link is missing.");
        Assert.True(mudJs.Success, "MudBlazor.min.js script is missing.");
        Assert.True(mudCss.Index < html.IndexOf("css/tokens.", StringComparison.Ordinal), "MudBlazor.min.css must precede tokens.css.");
        var blazor = html.IndexOf("_framework/blazor.web", StringComparison.Ordinal);
        Assert.True(blazor >= 0, "blazor.web.js script is missing.");
        Assert.True(mudJs.Index > blazor, "MudBlazor.min.js must follow blazor.web.js.");

        foreach (var href in new[] { mudCss.Groups["href"].Value, mudJs.Groups["src"].Value })
        {
            using var response = await client.GetAsync(href);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Fonts_css_declares_self_hosted_Roboto_with_swap()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");
        var fontsHref = Regex.Match(html, @"href=""(?<href>css/fonts(?:\.[a-z0-9]+)?\.css)""").Groups["href"].Value;
        var css = await client.GetStringAsync(fontsHref);

        var face = Regex.Match(css, @"@font-face\s*\{[^}]*font-family:\s*""Roboto""[^}]*\}");
        Assert.True(face.Success, "fonts.css has no Roboto @font-face.");
        Assert.Contains("font-display: swap", face.Value);
        Assert.Contains("font-weight: 100 900", face.Value);
        var url = Regex.Match(face.Value, @"url\(""(?<url>/fonts/[^""]+\.woff2)""\)").Groups["url"].Value;
        Assert.NotEmpty(url);

        using var font = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("font/woff2", font.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(@"<link rel=""icon"" href=""(?<href>favicon(?:\.[a-z0-9]+)?\.ico)"" sizes=""48x48""", "image/x-icon")]
    [InlineData(@"<link rel=""icon"" href=""(?<href>favicon(?:\.[a-z0-9]+)?\.svg)"" type=""image/svg\+xml""", "image/svg+xml")]
    [InlineData(@"<link rel=""apple-touch-icon"" href=""(?<href>apple-touch-icon(?:\.[a-z0-9]+)?\.png)""", "image/png")]
    [InlineData(@"<link rel=""manifest"" href=""(?<href>manifest(?:\.[a-z0-9]+)?\.webmanifest)""", "application/manifest+json")]
    public async Task Document_links_each_brand_icon_and_the_manifest_from_local_assets(
        string linkPattern, string mediaType)
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        // Blazor HTML-encodes some attribute characters (the "+" in image/svg+xml); match what a browser reads.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        var match = Regex.Match(html, linkPattern);
        Assert.True(match.Success, $"The document has no link matching {linkPattern}.");

        using var response = await client.GetAsync(match.Groups["href"].Value);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(body);
    }

    [Fact]
    public async Task Web_manifest_names_the_app_and_its_install_icons_resolve()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");
        var href = Regex.Match(html, @"<link rel=""manifest"" href=""(?<href>[^""]+)""").Groups["href"].Value;
        using var manifest = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(href));
        var root = manifest.RootElement;

        Assert.Equal("StreamKeepr", root.GetProperty("name").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());

        var sizes = new List<string>();
        foreach (var icon in root.GetProperty("icons").EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString()!;
            using var response = await client.GetAsync(src);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(icon.GetProperty("type").GetString(), response.Content.Headers.ContentType?.MediaType);
            sizes.Add(icon.GetProperty("sizes").GetString()!);
        }

        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
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
    public async Task Blazor_framework_script_is_served_from_the_build_manifest()
    {
        using var factory = new StreamKeeprAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");
        var match = Regex.Match(html, @"src=""(?<src>_framework/blazor\.web(?:\.[a-z0-9]+)?\.js)""");
        Assert.True(match.Success, "The Blazor framework script is missing from the document.");

        using var response = await client.GetAsync(match.Groups["src"].Value);
        var script = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(script));
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
        Assert.Contains("data-shell", html);
        Assert.Contains("data-slot=\"player\"", html);
        Assert.Contains("data-slot=\"stats\"", html);
        Assert.Contains("data-slot=\"details\"", html);
        Assert.True(
            html.IndexOf("data-slot=\"player\"", StringComparison.Ordinal) <
            html.IndexOf("data-slot=\"details\"", StringComparison.Ordinal));
        Assert.True(
            html.IndexOf("data-slot=\"details\"", StringComparison.Ordinal) <
            html.IndexOf("data-slot=\"stats\"", StringComparison.Ordinal));
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
        Assert.Contains("data-drawer=\"rail\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
    }
}
