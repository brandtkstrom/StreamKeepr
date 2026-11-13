using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreThumbnailTests : IDisposable
{
    private readonly SessionStoreTestContext _context = new();

    public void Dispose() => _context.Dispose();

    private string Image(params byte[] bytes)
    {
        Directory.CreateDirectory(_context.Root);
        string path = Path.Combine(_context.Root, Guid.NewGuid().ToString("N") + ".jpg.tmp");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Save_thumbnail_moves_the_image_into_the_session_and_updates_the_library()
    {
        await _context.Store.OpenAsync(_context.Session, _context.Live, CancellationToken.None);
        Assert.False(_context.Library.Find(_context.Session.Id)!.HasThumbnail);
        string image = Image(1, 2, 3);

        bool saved = await _context.Store.SaveThumbnailAsync(_context.Directory, image, CancellationToken.None);

        Assert.True(saved);
        Assert.False(File.Exists(image));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(_context.ThumbnailPath));
        RecordingSummary summary = _context.Library.Find(_context.Session.Id)!;
        Assert.True(summary.HasThumbnail);
        Assert.Equal($"/media/streamer/{_context.Session.Id}/thumb.jpg", summary.ThumbnailUrl);
    }

    [Fact]
    public async Task Save_thumbnail_never_replaces_an_existing_thumbnail()
    {
        await _context.Store.OpenAsync(_context.Session, _context.Live, CancellationToken.None);
        await File.WriteAllBytesAsync(_context.ThumbnailPath, [9]);
        string image = Image(1, 2, 3);

        bool saved = await _context.Store.SaveThumbnailAsync(_context.Directory, image, CancellationToken.None);

        Assert.False(saved);
        Assert.False(File.Exists(image));
        Assert.Equal([9], await File.ReadAllBytesAsync(_context.ThumbnailPath));
    }

    [Fact]
    public async Task Save_thumbnail_discards_the_image_when_the_session_is_gone()
    {
        string image = Image(1, 2, 3);

        bool saved = await _context.Store.SaveThumbnailAsync(_context.Directory, image, CancellationToken.None);

        Assert.False(saved);
        Assert.False(File.Exists(image));
        Assert.False(Directory.Exists(_context.Directory));
        Assert.Null(_context.Library.Find(_context.Session.Id));
    }
}
