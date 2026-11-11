using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

internal sealed class FakeDiskSpace(long freeBytes, long totalBytes) : IDiskSpace
{
    public long AvailableFreeSpace(string path) => freeBytes;

    public long TotalSize(string path) => totalBytes;
}
