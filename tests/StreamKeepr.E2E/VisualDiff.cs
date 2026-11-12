using System.Text.Json;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// Pixel comparison of two PNG screenshots, run on a canvas in the browser so the suite needs
/// no image library. A pixel differs when any colour channel moves by more than
/// <see cref="ChannelTolerance"/>, which ignores sub-pixel anti-aliasing noise. Testing
/// contract §8: a screen fails when more than 0.1 % of its pixels differ.
/// </summary>
internal sealed record VisualDiff(
    bool SizeMatches,
    int Width,
    int Height,
    int DifferentPixels,
    byte[]? DiffPng)
{
    public const double MaxDifferentRatio = 0.001;
    public const int ChannelTolerance = 16;

    public double DifferentRatio =>
        Width * Height == 0 ? 1 : (double)DifferentPixels / (Width * Height);

    public bool Exceeds(double maxRatio) => !SizeMatches || DifferentRatio > maxRatio;

    /// <summary>
    /// Compares <paramref name="expected"/> with <paramref name="actual"/> on
    /// <paramref name="page"/>, which may be any page (it is only used for its canvas). The
    /// diff image shows differing pixels in red over a faded copy of the expected image.
    /// </summary>
    public static async Task<VisualDiff> CompareAsync(IPage page, byte[] expected, byte[] actual)
    {
        JsonElement result = await page.EvaluateAsync<JsonElement>("""
            async ([expected, actual, tolerance]) => {
              const decode = async base64 => createImageBitmap(
                await (await fetch(`data:image/png;base64,${base64}`)).blob());
              const [a, b] = await Promise.all([decode(expected), decode(actual)]);
              if (a.width !== b.width || a.height !== b.height) {
                return { sizeMatches: false, width: b.width, height: b.height, different: 0, diff: null };
              }
              const { width, height } = a;
              const pixels = bitmap => {
                const context = new OffscreenCanvas(width, height).getContext('2d');
                context.drawImage(bitmap, 0, 0);
                return context.getImageData(0, 0, width, height).data;
              };
              const pa = pixels(a);
              const pb = pixels(b);
              const canvas = new OffscreenCanvas(width, height);
              const context = canvas.getContext('2d');
              const out = context.createImageData(width, height);
              let different = 0;
              for (let i = 0; i < pa.length; i += 4) {
                const delta = Math.max(
                  Math.abs(pa[i] - pb[i]), Math.abs(pa[i + 1] - pb[i + 1]),
                  Math.abs(pa[i + 2] - pb[i + 2]), Math.abs(pa[i + 3] - pb[i + 3]));
                if (delta > tolerance) {
                  different++;
                  out.data.set([255, 0, 0, 255], i);
                } else {
                  const faded = 255 - (255 - (pa[i] + pa[i + 1] + pa[i + 2]) / 3) * 0.25;
                  out.data.set([faded, faded, faded, 255], i);
                }
              }
              context.putImageData(out, 0, 0);
              const bytes = new Uint8Array(await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer());
              let binary = '';
              for (let i = 0; i < bytes.length; i += 0x8000) {
                binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
              }
              return { sizeMatches: true, width, height, different, diff: btoa(binary) };
            }
            """,
            new object[] { Convert.ToBase64String(expected), Convert.ToBase64String(actual), ChannelTolerance });

        string? diff = result.GetProperty("diff").GetString();
        return new VisualDiff(
            result.GetProperty("sizeMatches").GetBoolean(),
            result.GetProperty("width").GetInt32(),
            result.GetProperty("height").GetInt32(),
            result.GetProperty("different").GetInt32(),
            diff is null ? null : Convert.FromBase64String(diff));
    }
}
