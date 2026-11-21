using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

public sealed class LivePlaylistConcurrencyTests
{
    [Fact]
    public async Task Concurrent_writer_and_readers_only_see_complete_growing_playlists()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();

        var cache = new LivePlaylistCache();
        var service = new LivePlaylistService(
            context.Library,
            cache,
            new SessionStoreLivePlaylistIo(
                context.Store,
                Options.Create(new StorageOptions { RecordingsRoot = context.Root })),
            Options.Create(new LivePlaylistOptions()),
            context.Clock,
            NullLogger<LivePlaylistService>.Instance);
        RecordingSummary summary = Assert.IsType<RecordingSummary>(
            context.Library.Find(context.Session.Id));
        Assert.NotNull(await service.RefreshOneAsync(summary, CancellationToken.None));

        string path = context.PartPath(1);
        string temp = path + ".tmp";
        const int totalSegments = 24;

        Task writer = Task.Run(async () =>
        {
            for (int count = 2; count <= totalSegments; count++)
            {
                string content = LivePlaylist(6, "init_001.mp4",
                    Enumerable.Range(1, count)
                        .Select(n => Segment(
                            $"p001_{n:D5}.m4s",
                            $"2026-11-10T18:30:{n * 2 % 60:00}Z",
                            6))
                        .ToArray());
                await File.WriteAllTextAsync(temp, content);
                await MoveWithRetryAsync(temp, path);
                await Task.Delay(1);
            }
        });

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            int lastCount = 0;
            for (int i = 0; i < 40; i++)
            {
                string? body = await service.RefreshOneAsync(summary, CancellationToken.None);
                if (body is not null)
                {
                    Assert.StartsWith("#EXTM3U", body, StringComparison.Ordinal);
                    Assert.DoesNotContain("#EXT-X-ENDLIST", body, StringComparison.Ordinal);
                    int count = PlaylistTimeline.Parse(body).SegmentCount;
                    Assert.True(
                        count >= lastCount,
                        $"segment count went from {lastCount} to {count}");
                    lastCount = count;
                }

                await Task.Delay(2);
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(30));

        string? final = await service.RefreshOneAsync(summary, CancellationToken.None);
        Assert.NotNull(final);
        string finalContent = await File.ReadAllTextAsync(path);
        Assert.Equal(
            PlaylistStitcher.Stitch([new PartPlaylist(1, finalContent)], 12, final: false),
            final);
        Assert.Equal(totalSegments, PlaylistTimeline.Parse(final).SegmentCount);
    }

    /// <summary>
    /// Models FFmpeg's temp-file rename. Windows may reject the replace while a reader still
    /// holds the final path, so the writer retries; the reader never caches a handle.
    /// </summary>
    private static async Task MoveWithRetryAsync(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 100)
                    throw;
                await Task.Delay(2);
            }
        }
    }
}
