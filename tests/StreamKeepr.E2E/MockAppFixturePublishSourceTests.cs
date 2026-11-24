namespace StreamKeepr.E2E;

/// <summary>
/// CI publishes the app once and hands the fixture that folder; a local run publishes on its
/// own. These run without a browser or app, so they are not tagged E2E.
/// </summary>
public sealed class MockAppFixturePublishSourceTests : IDisposable
{
    private readonly string _root = MockAppFixture.SuiteTempPath($"source-{Guid.NewGuid():N}");

    public MockAppFixturePublishSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_supplied_publish_the_fixture_publishes_into_its_own_folder(string? configured)
    {
        string fallback = Path.Combine(_root, "own");

        MockAppFixture.PublishSource source = MockAppFixture.ResolvePublishSource(configured, fallback);

        Assert.Equal(fallback, source.Root);
        Assert.True(source.PublishFirst);
    }

    [Fact]
    public void A_supplied_publish_is_used_as_is_without_publishing_again()
    {
        string supplied = Path.Combine(_root, "ci");
        Directory.CreateDirectory(Path.Combine(supplied, "wwwroot"));
        File.WriteAllBytes(Path.Combine(supplied, "StreamKeepr.dll"), [1]);

        MockAppFixture.PublishSource source =
            MockAppFixture.ResolvePublishSource(supplied, Path.Combine(_root, "own"));

        Assert.Equal(supplied, source.Root);
        Assert.False(source.PublishFirst);
    }

    [Fact]
    public void A_supplied_publish_without_the_app_fails_up_front_and_names_the_folder()
    {
        string supplied = Path.Combine(_root, "empty");
        Directory.CreateDirectory(supplied);

        var error = Assert.Throws<InvalidOperationException>(
            () => MockAppFixture.ResolvePublishSource(supplied, Path.Combine(_root, "own")));

        Assert.Contains(supplied, error.Message, StringComparison.Ordinal);
        Assert.Contains("STREAMKEEPR_E2E_PUBLISH_DIR", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_supplied_publish_with_a_torn_compressed_asset_fails_up_front()
    {
        string supplied = Path.Combine(_root, "torn");
        Directory.CreateDirectory(Path.Combine(supplied, "wwwroot"));
        File.WriteAllBytes(Path.Combine(supplied, "StreamKeepr.dll"), [1]);
        File.WriteAllBytes(Path.Combine(supplied, "wwwroot", "app.css.br"), []);

        var error = Assert.Throws<InvalidOperationException>(
            () => MockAppFixture.ResolvePublishSource(supplied, Path.Combine(_root, "own")));

        Assert.Contains("app.css.br", error.Message, StringComparison.Ordinal);
    }
}
