using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class PartPlaylistReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Read_does_not_block_the_recorder_writing_or_deleting_the_file()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "p001.m3u8");
        await File.WriteAllTextAsync(path, "old body");

        await using FileStream stream = PartPlaylistReader.OpenSharedRead(path);
        Assert.True(stream.IsAsync);

        // The recorder writes part playlists in place. Read/write sharing lets that write
        // proceed while a refresh still holds the file open.
        await using (var writer = new FileStream(
            path,
            FileMode.Truncate,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous))
        {
            await writer.WriteAsync("new body"u8.ToArray());
        }

        // Delete sharing is what lets a delete or a replace clean up the folder.
        File.Delete(path);

        // The open handle still reads the file after the delete; the writer was never
        // blocked by the live reader.
        using var reader = new StreamReader(stream);
        Assert.Equal("new body", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Atomic_replace_over_an_open_reader_is_best_effort()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "p001.m3u8");
        string replacement = path + ".tmp";
        await File.WriteAllTextAsync(path, "old body");
        await File.WriteAllTextAsync(replacement, "new body");

        await using (FileStream stream = PartPlaylistReader.OpenSharedRead(path))
        {
            Assert.True(stream.IsAsync);

            if (OperatingSystem.IsWindows())
            {
                // Windows refuses MoveFileEx over an open read handle even with delete
                // sharing. The store retries the atomic replace after such a sharing
                // violation; POSIX filesystems allow the replace outright.
                Exception? error = Record.Exception(
                    () => File.Move(replacement, path, overwrite: true));
                Assert.True(error is UnauthorizedAccessException or IOException);
            }
            else
            {
                File.Move(replacement, path, overwrite: true);
                Assert.Equal("new body", await File.ReadAllTextAsync(path));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(replacement));
            File.Move(replacement, path, overwrite: true);
            Assert.Equal("new body", await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task Try_read_returns_null_for_a_missing_file()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "missing.m3u8");

        Assert.Null(await PartPlaylistReader.TryReadAllTextAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Read_all_text_reads_the_whole_file()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "p001.m3u8");
        await File.WriteAllTextAsync(path, "#EXTM3U\np001_00001.m4s\n");

        Assert.Equal(
            "#EXTM3U\np001_00001.m4s\n",
            await PartPlaylistReader.ReadAllTextAsync(path, CancellationToken.None));
    }
}
