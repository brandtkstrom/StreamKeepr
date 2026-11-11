namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Free and total bytes for the volume that holds the recordings root.</summary>
public interface IDiskSpace
{
    long AvailableFreeSpace(string path);

    long TotalSize(string path);
}

/// <summary>Reads the real filesystem through <see cref="DriveInfo"/>.</summary>
public sealed class DriveInfoDiskSpace : IDiskSpace
{
    public long AvailableFreeSpace(string path) => Drive(path).AvailableFreeSpace;

    public long TotalSize(string path) => Drive(path).TotalSize;

    private static DriveInfo Drive(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DriveInfo(Path.GetFullPath(path));
    }
}
