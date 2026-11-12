using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// The screenshot comparison behind the visual baselines (#106): it counts differing pixels
/// in the browser, so the suite needs no image library.
/// </summary>
[Trait("Category", "E2E")]
public sealed class VisualDiffTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        _page = await _browser.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task Identical_images_have_no_differing_pixels()
    {
        byte[] image = await PngAsync(100, 100, changedPixels: 0);

        VisualDiff diff = await VisualDiff.CompareAsync(_page, image, image);

        Assert.True(diff.SizeMatches);
        Assert.Equal(0, diff.DifferentPixels);
        Assert.False(diff.Exceeds(VisualDiff.MaxDifferentRatio));
    }

    [Fact]
    public async Task One_pixel_in_ten_thousand_is_within_the_budget()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 100, changedPixels: 1));

        Assert.Equal(1, diff.DifferentPixels);
        Assert.False(diff.Exceeds(VisualDiff.MaxDifferentRatio));
        Assert.NotNull(diff.DiffPng);
    }

    [Fact]
    public async Task Twenty_pixels_in_ten_thousand_fail_the_budget()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 100, changedPixels: 20));

        Assert.Equal(20, diff.DifferentPixels);
        Assert.True(diff.Exceeds(VisualDiff.MaxDifferentRatio));
    }

    [Fact]
    public async Task Anti_aliasing_noise_below_the_tolerance_is_ignored()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 100, 0, shade: 8));

        Assert.Equal(0, diff.DifferentPixels);
    }

    [Fact]
    public async Task A_different_size_always_fails()
    {
        VisualDiff diff = await VisualDiff.CompareAsync(
            _page, await PngAsync(100, 100, 0), await PngAsync(100, 101, 0));

        Assert.False(diff.SizeMatches);
        Assert.True(diff.Exceeds(VisualDiff.MaxDifferentRatio));
    }

    /// <summary>
    /// A grey image, optionally lightened by <paramref name="shade"/>, with the first
    /// <paramref name="changedPixels"/> pixels painted red.
    /// </summary>
    private async Task<byte[]> PngAsync(int width, int height, int changedPixels, int shade = 0)
    {
        string base64 = await _page.EvaluateAsync<string>("""
            async ([width, height, changed, shade]) => {
              const canvas = new OffscreenCanvas(width, height);
              const context = canvas.getContext('2d');
              const grey = 128 + shade;
              context.fillStyle = `rgb(${grey}, ${grey}, ${grey})`;
              context.fillRect(0, 0, width, height);
              context.fillStyle = 'rgb(255, 0, 0)';
              for (let i = 0; i < changed; i++) context.fillRect(i % width, Math.floor(i / width), 1, 1);
              const bytes = new Uint8Array(await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer());
              let binary = '';
              for (const b of bytes) binary += String.fromCharCode(b);
              return btoa(binary);
            }
            """, new object[] { width, height, changedPixels, shade });
        return Convert.FromBase64String(base64);
    }
}
