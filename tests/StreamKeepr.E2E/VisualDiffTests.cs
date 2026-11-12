using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// The screenshot comparison behind the visual baselines (#106): it counts differing pixels
/// in the browser, so the suite needs no image library.
/// </summary>
[Trait("Category", "E2E")]
public sealed class VisualDiffTests(VisualDiffTests.BrowserPage browser)
    : IClassFixture<VisualDiffTests.BrowserPage>
{
    private readonly IPage _page = browser.Page;

    [Fact]
    public async Task Identical_images_have_no_differing_pixels()
    {
        byte[] image = await PngAsync(100, 100, changedPixels: 0);

        VisualDiff diff = await VisualDiff.CompareAsync(_page, image, image);

        Assert.True(diff.SizeMatches);
        Assert.Equal(0, diff.DifferentPixels);
        Assert.False(diff.Exceeds());
    }

    [Fact]
    public async Task One_pixel_in_ten_thousand_is_within_the_budget()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 100, changedPixels: 1));

        Assert.Equal(1, diff.DifferentPixels);
        Assert.False(diff.Exceeds());
        Assert.NotNull(diff.DiffPng);
    }

    [Fact]
    public async Task Twenty_pixels_in_ten_thousand_fail_the_budget()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 100, changedPixels: 20));

        Assert.Equal(20, diff.DifferentPixels);
        Assert.True(diff.Exceeds());
    }

    [Fact]
    public async Task A_shift_at_the_channel_tolerance_is_ignored()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page,
            await PngAsync(100, 100, 0),
            await PngAsync(100, 100, 0, shade: VisualDiff.ChannelTolerance));

        Assert.Equal(0, diff.DifferentPixels);
    }

    [Fact]
    public async Task A_shift_just_past_the_channel_tolerance_counts_every_pixel()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page,
            await PngAsync(100, 100, 0),
            await PngAsync(100, 100, 0, shade: VisualDiff.ChannelTolerance + 1));

        Assert.Equal(100 * 100, diff.DifferentPixels);
        Assert.True(diff.Exceeds());
    }

    [Fact]
    public async Task A_different_size_always_fails()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 101, 0));

        Assert.False(diff.SizeMatches);
        Assert.True(diff.Exceeds());
    }

    /// <summary>
    /// A grey image, optionally lightened by <paramref name="shade"/>, with the first
    /// <paramref name="changedPixels"/> pixels painted red.
    /// </summary>
    private async Task<byte[]> PngAsync(int width, int height, int changedPixels, int shade = 0)
    {
        string base64 = await _page.EvaluateAsync<string>($$"""
            async ([width, height, changed, shade]) => {
              const toBase64 = {{VisualDiff.CanvasToBase64Js}};
              const canvas = new OffscreenCanvas(width, height);
              const context = canvas.getContext('2d');
              const grey = 128 + shade;
              context.fillStyle = `rgb(${grey}, ${grey}, ${grey})`;
              context.fillRect(0, 0, width, height);
              context.fillStyle = 'rgb(255, 0, 0)';
              for (let i = 0; i < changed; i++) context.fillRect(i % width, Math.floor(i / width), 1, 1);
              return toBase64(canvas);
            }
            """, new object[] { width, height, changedPixels, shade });
        return Convert.FromBase64String(base64);
    }

    /// <summary>One Chrome page shared by the class, so the tests launch the browser once.</summary>
    public sealed class BrowserPage : IAsyncLifetime
    {
        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;

        public IPage Page { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
            Page = await _browser.NewPageAsync();
        }

        public async Task DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
    }
}
