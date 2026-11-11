using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class DiskGuardTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(1, Gib - 1, false)]
    [InlineData(1, Gib, true)]
    [InlineData(2, (2 * Gib) - 1, false)]
    [InlineData(2, 2 * Gib, true)]
    public void HasRoom_uses_the_gib_boundary(int minFreeGb, long freeBytes, bool expected)
    {
        Assert.Equal(expected, DiskGuard.HasRoom(freeBytes, minFreeGb));
    }

    [Fact]
    public void Free_total_and_the_recording_threshold_come_from_the_disk_space_seam()
    {
        var storage = Options.Create(new StorageOptions
        {
            RecordingsRoot = "/recordings",
            MinFreeGb = 5,
        });
        var guard = new DiskGuard(storage, new FakeDiskSpace(freeBytes: 5 * Gib, totalBytes: 10 * Gib));

        Assert.Equal(5 * Gib, guard.FreeBytes);
        Assert.Equal(10 * Gib, guard.TotalBytes);
        Assert.True(guard.HasRoomToRecord());

        var full = new DiskGuard(
            Options.Create(new StorageOptions { RecordingsRoot = "/recordings", MinFreeGb = 6 }),
            new FakeDiskSpace(freeBytes: 5 * Gib, totalBytes: 10 * Gib));

        Assert.False(full.HasRoomToRecord());
    }

    [Fact]
    public void DriveInfoDiskSpace_reads_a_real_temp_root()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var disk = new DriveInfoDiskSpace();

            Assert.True(disk.AvailableFreeSpace(root) > 0);
            Assert.True(disk.TotalSize(root) > 0);
            Assert.True(disk.AvailableFreeSpace(root) <= disk.TotalSize(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
