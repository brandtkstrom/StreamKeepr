using System.Net;
using System.Net.Http.Headers;
using StreamKeepr.Configuration;

namespace StreamKeepr.Tests.Hosting;

public sealed class RecordingsMediaTests
{
    private const string Sentinel = "top-secret-recording-metadata";

    [Fact]
    public void Recordings_root_is_created_at_startup()
    {
        using var factory = new StreamKeeprAppFactory();
        Assert.False(Directory.Exists(factory.RecordingsRoot));

        using var client = factory.CreateClient();

        Assert.True(Directory.Exists(factory.RecordingsRoot));
    }

    [Fact]
    public void Unwritable_recordings_root_fails_startup_with_ownership_guidance()
    {
        string root = Path.Combine(Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "");

        try
        {
            using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
            {
                [$"{StorageOptions.SectionName}:RecordingsRoot"] = root,
            });

            var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            Assert.Contains("UID 1654", error.Message);
        }
        finally
        {
            File.Delete(root);
        }
    }

    [Theory]
    [InlineData(".m3u8", "application/vnd.apple.mpegurl", "no-cache")]
    [InlineData(".m4s", "video/iso.segment", "public, max-age=31536000, immutable")]
    [InlineData(".mp4", "video/mp4", "public, max-age=31536000, immutable")]
    [InlineData(".jpg", "image/jpeg", "public, max-age=31536000, immutable")]
    [InlineData(".png", "image/png", "public, max-age=31536000, immutable")]
    public async Task Allowed_media_is_served_with_explicit_content_type_and_cache_policy(
        string extension, string expectedContentType, string expectedCacheControl)
    {
        using var factory = new StreamKeeprAppFactory();
        string fileName = $"index{extension}";
        WriteFile(factory, $"channel/session/{fileName}", "0123456789");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/media/channel/session/{fileName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(expectedCacheControl, response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("0123456789", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("session.json")]
    [InlineData("channel/session/session.json")]
    [InlineData("channel/session/admin-tokens.json")]
    [InlineData("channel/session/progress.json")]
    [InlineData("channel/session/index.m3u8.tmp")]
    [InlineData("channel/session/p001.m4s.tmp")]
    [InlineData("channel/session/notes.txt")]
    [InlineData("channel/session/thumbnail.webp")]
    [InlineData("channel/session/no-extension")]
    public async Task Non_allowlisted_files_return_404_without_revealing_content(string relativePath)
    {
        using var factory = new StreamKeeprAppFactory();
        WriteFile(factory, relativePath, Sentinel);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/media/{relativePath}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(".streamkeepr/exports/export.mp4")]
    [InlineData(".streamkeepr/keys/key.png")]
    [InlineData("channel/.hidden/index.mp4")]
    [InlineData("channel/session/.hidden.mp4")]
    [InlineData("channel/session/.secret.png")]
    [InlineData("channel/session/.tmp/thumb.png")]
    public async Task Dot_prefixed_segments_return_404_without_revealing_content(string relativePath)
    {
        using var factory = new StreamKeeprAppFactory();
        WriteFile(factory, relativePath, Sentinel);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/media/{relativePath}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/media/../session.json")]
    [InlineData("/media/%2e%2e/session.json")]
    [InlineData("/media/%2E%2E/session.json")]
    [InlineData("/media/%2e%2e%2fsession.json")]
    [InlineData("/media/%252e%252e%252fsession.json")]
    [InlineData("/media/channel/session/%2e%2e%2f%2e%2e%2f%2e%2e%2fsession.json")]
    [InlineData("/media/channel/session/x%2f..%2f..%2fsecret.mp4")]
    [InlineData("/media/channel/session/%2e%2e%5c%2e%2e%5csecret.mp4")]
    [InlineData("/media/%2e%2e%5csecret.mp4")]
    public async Task Traversal_returns_404_without_revealing_outside_files(string url)
    {
        using var factory = new StreamKeeprAppFactory();
        WriteFile(factory, "session.json", Sentinel);
        WriteFile(factory, "secret.mp4", Sentinel);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("..%2fsecret.mp4")]
    [InlineData("%252e%252e%252fsecret.mp4")]
    [InlineData("..%5csecret.mp4")]
    [InlineData("x%2f..%2f..%2fsecret.mp4")]
    public async Task Encoded_traversal_is_rejected_even_when_a_literal_file_matches(string literalName)
    {
        using var factory = new StreamKeeprAppFactory();
        WriteFile(factory, $"channel/session/{literalName}", Sentinel);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/media/channel/session/{literalName}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/media")]
    [InlineData("/media/")]
    [InlineData("/media/channel")]
    [InlineData("/media/channel/session")]
    [InlineData("/media/channel/session/")]
    [InlineData("/media/channel/session/missing.m3u8")]
    [InlineData("/media/channel/session/missing.m4s")]
    [InlineData("/media/channel/session/missing.mp4")]
    [InlineData("/media/channel/session/missing.jpg")]
    [InlineData("/media/channel/session/missing.png")]
    public async Task Missing_files_and_directories_return_404(string url)
    {
        using var factory = new StreamKeeprAppFactory();
        WriteFile(factory, "channel/session/p001.m4s", "0123456789");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("p001.m4s", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Directory_names_do_not_produce_a_listing_or_500()
    {
        using var factory = new StreamKeeprAppFactory();
        Directory.CreateDirectory(Path.Combine(factory.RecordingsRoot, "channel/session/fake.mp4"));
        WriteFile(factory, "channel/session/fake.mp4/child.txt", Sentinel);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/media/channel/session/fake.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("child.txt", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_head_and_range_are_supported()
    {
        const string content = "0123456789";
        using var factory = new StreamKeeprAppFactory();
        WriteFile(factory, "channel/session/p001.m4s", content);
        using var client = factory.CreateClient();
        const string url = "/media/channel/session/p001.m4s";

        using var get = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(content, await get.Content.ReadAsStringAsync());
        Assert.Equal(content.Length, get.Content.Headers.ContentLength);
        Assert.Equal("bytes", get.Headers.AcceptRanges.Single());

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var head = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(content.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
        using var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("bytes 2-5/10", range.Content.Headers.ContentRange?.ToString());
        Assert.Equal("2345", await range.Content.ReadAsStringAsync());

        using var missingHeadRequest = new HttpRequestMessage(HttpMethod.Head, "/media/channel/session/missing.m4s");
        using var missingHead = await client.SendAsync(missingHeadRequest);
        Assert.Equal(HttpStatusCode.NotFound, missingHead.StatusCode);
    }

    private static void WriteFile(StreamKeeprAppFactory factory, string relativePath, string content)
    {
        string path = Path.Combine(factory.RecordingsRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
