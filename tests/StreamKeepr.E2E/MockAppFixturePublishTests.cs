namespace StreamKeepr.E2E;

/// <summary>
/// The publish guard runs without a browser or app, so these tests are not tagged E2E and run
/// with the fast suite.
/// </summary>
public sealed class MockAppFixturePublishTests : IDisposable
{
    private readonly string _webRoot = MockAppFixture.SuiteTempPath($"guard-{Guid.NewGuid():N}");

    public MockAppFixturePublishTests() => Directory.CreateDirectory(Path.Combine(_webRoot, "lib"));

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    [Fact]
    public void A_torn_compressed_asset_fails_the_publish_and_names_the_file()
    {
        File.WriteAllBytes(Path.Combine(_webRoot, "lib", "hls.abc.mjs"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_webRoot, "lib", "hls.abc.mjs.br"), []);
        File.WriteAllBytes(Path.Combine(_webRoot, "lib", "hls.abc.mjs.gz"), [0x1f, 0x8b]);

        var error = Assert.Throws<InvalidOperationException>(
            () => MockAppFixture.EnsureNoEmptyCompressedAssets(_webRoot));

        Assert.Contains(Path.Combine("lib", "hls.abc.mjs.br"), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".gz", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_source_file_is_allowed_when_its_compressed_copies_are_not_empty()
    {
        File.WriteAllBytes(Path.Combine(_webRoot, "empty.css"), []);
        File.WriteAllBytes(Path.Combine(_webRoot, "empty.css.br"), [0x3b]);

        MockAppFixture.EnsureNoEmptyCompressedAssets(_webRoot);
    }
}
