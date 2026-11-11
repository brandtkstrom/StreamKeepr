using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class PartFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "streamkeepr-tests",
        Guid.NewGuid().ToString("N"));

    public PartFilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Names_use_zero_padded_part_numbers()
    {
        Assert.Equal("p001.m3u8", PartFiles.Playlist(1));
        Assert.Equal("init_001.mp4", PartFiles.Init(1));
        Assert.Equal("p001_%05d.m4s", PartFiles.SegmentPattern(1));
        Assert.Equal("p042.m3u8", PartFiles.Playlist(42));
        Assert.Equal("init_042.mp4", PartFiles.Init(42));
        Assert.Equal("p1000.m3u8", PartFiles.Playlist(1000));
    }

    [Fact]
    public void Delete_removes_only_the_target_part_including_temporary_files()
    {
        string[] targetFiles =
        [
            "p001.m3u8",
            "p001.m3u8.tmp",
            "p001_00000.m4s",
            "p001_00001.m4s.tmp",
            "init_001.mp4",
            "init_001.mp4.tmp",
        ];
        string[] otherFiles = ["p002.m3u8", "p002_00000.m4s", "init_002.mp4", "notes.txt"];

        foreach (string file in targetFiles.Concat(otherFiles))
            File.WriteAllText(Path.Combine(_directory, file), file);

        PartFiles.Delete(_directory, 1);

        foreach (string file in targetFiles)
            Assert.False(File.Exists(Path.Combine(_directory, file)), $"{file} should be deleted");

        foreach (string file in otherFiles)
            Assert.True(File.Exists(Path.Combine(_directory, file)), $"{file} should be kept");
    }

    [Fact]
    public void Delete_does_nothing_when_the_directory_is_missing()
    {
        PartFiles.Delete(Path.Combine(_directory, "missing"), 1);
    }

    [Theory]
    [InlineData("p001.m3u8")]
    [InlineData("p001.m3u8.tmp")]
    [InlineData("p001_00000.m4s")]
    [InlineData("p001_00000.m4s.tmp")]
    [InlineData("init_001.mp4")]
    [InlineData("init_001.mp4.tmp")]
    public void Exists_detects_every_file_belonging_to_the_part(string file)
    {
        File.WriteAllText(Path.Combine(_directory, file), file);

        Assert.True(PartFiles.Exists(_directory, 1));
        Assert.False(PartFiles.Exists(_directory, 2));
    }

    [Fact]
    public void Exists_is_false_for_an_empty_or_missing_directory()
    {
        Assert.False(PartFiles.Exists(_directory, 1));
        Assert.False(PartFiles.Exists(Path.Combine(_directory, "missing"), 1));
    }

    [Fact]
    public void HasSegments_ignores_playlists_init_and_temporary_segments()
    {
        File.WriteAllText(Path.Combine(_directory, "p001.m3u8"), "playlist");
        File.WriteAllText(Path.Combine(_directory, "init_001.mp4"), "init");
        File.WriteAllText(Path.Combine(_directory, "p001_00000.m4s.tmp"), "partial");

        Assert.False(PartFiles.HasSegments(_directory, 1));

        File.WriteAllText(Path.Combine(_directory, "p001_00000.m4s"), "segment");

        Assert.True(PartFiles.HasSegments(_directory, 1));
        Assert.False(PartFiles.HasSegments(_directory, 2));
    }

    [Fact]
    public void TotalBytes_counts_init_and_completed_segments_only()
    {
        File.WriteAllText(Path.Combine(_directory, "init_001.mp4"), "1234");
        File.WriteAllText(Path.Combine(_directory, "p001_00000.m4s"), "123456");
        File.WriteAllText(Path.Combine(_directory, "p001_00001.m4s"), "12");
        File.WriteAllText(Path.Combine(_directory, "p001_00002.m4s.tmp"), "123456789");
        File.WriteAllText(Path.Combine(_directory, "p001.m3u8"), "playlist");

        Assert.Equal(12, PartFiles.TotalBytes(_directory, 1));
        Assert.Equal(0, PartFiles.TotalBytes(_directory, 2));
        Assert.Equal(0, PartFiles.TotalBytes(Path.Combine(_directory, "missing"), 1));
    }
}
