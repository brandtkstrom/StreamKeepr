using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Answers whether the recordings volume has enough free space to start another part.
/// The coordinator checks this before every part; a full disk ends the part normally.
/// </summary>
public sealed class DiskGuard(IOptions<StorageOptions> storage, IDiskSpace diskSpace)
{
    public const long BytesPerGib = 1024L * 1024 * 1024;

    public long FreeBytes => diskSpace.AvailableFreeSpace(storage.Value.RecordingsRoot);

    public long TotalBytes => diskSpace.TotalSize(storage.Value.RecordingsRoot);

    public bool HasRoomToRecord() => HasRoom(FreeBytes, storage.Value.MinFreeGb);

    public static bool HasRoom(long freeBytes, int minFreeGb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(freeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(minFreeGb, 1);
        return freeBytes >= minFreeGb * BytesPerGib;
    }
}
