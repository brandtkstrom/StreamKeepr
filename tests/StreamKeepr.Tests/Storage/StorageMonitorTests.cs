using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Storage;

public sealed class StorageMonitorTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Samples_on_construction_and_exposes_free_and_total()
    {
        var disk = new MutableDiskSpace(freeBytes: 25, totalBytes: 100);
        using StorageMonitor monitor = Create(disk, out _, out _);

        Assert.Equal(new StorageView(25, 100), monitor.Current);
    }

    [Fact]
    public void Samples_every_sixty_seconds_and_ignores_moves_of_one_percent_or_less()
    {
        var disk = new MutableDiskSpace(freeBytes: 500, totalBytes: 1000);
        using StorageMonitor monitor = Create(disk, out FakeTimeProvider clock, out _);
        int changed = 0;
        monitor.Changed += () => changed++;

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, changed);

        disk.FreeBytes = 490; // exactly 1 % of total
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, changed);
        Assert.Equal(500, monitor.Current.FreeBytes);

        disk.FreeBytes = 489; // just over 1 %
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, changed);
        Assert.Equal(489, monitor.Current.FreeBytes);

        disk.FreeBytes = 480; // under 1 % from the new sample
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Total_change_counts_as_a_storage_change()
    {
        var disk = new MutableDiskSpace(freeBytes: 500, totalBytes: 1000);
        using StorageMonitor monitor = Create(disk, out FakeTimeProvider clock, out _);
        int changed = 0;
        monitor.Changed += () => changed++;

        disk.TotalBytes = 2000;
        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(1, changed);
        Assert.Equal(2000, monitor.Current.TotalBytes);
    }

    [Fact]
    public void Library_changes_refresh_the_volume_after_finalize_or_delete()
    {
        var disk = new MutableDiskSpace(freeBytes: 500, totalBytes: 1000);
        using StorageMonitor monitor = Create(disk, out _, out RecordingLibrary library);
        int changed = 0;
        monitor.Changed += () => changed++;

        disk.FreeBytes = 470;
        library.Upsert(Summary("session"));

        Assert.Equal(1, changed);
        Assert.Equal(470, monitor.Current.FreeBytes);
    }

    [Fact]
    public void Dispose_stops_the_timer_and_unsubscribes_from_the_library()
    {
        var disk = new MutableDiskSpace(freeBytes: 500, totalBytes: 1000);
        StorageMonitor monitor = Create(disk, out FakeTimeProvider clock, out RecordingLibrary library);
        int changed = 0;
        monitor.Changed += () => changed++;

        monitor.Dispose();
        disk.FreeBytes = 100;
        clock.Advance(TimeSpan.FromSeconds(120));
        library.Upsert(Summary("session"));

        Assert.Equal(0, changed);
    }

    private static StorageMonitor Create(
        MutableDiskSpace disk,
        out FakeTimeProvider clock,
        out RecordingLibrary library)
    {
        var options = Options.Create(new StorageOptions
        {
            RecordingsRoot = Path.GetTempPath(),
            MinFreeGb = 1,
        });

        clock = new FakeTimeProvider(T0);
        library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);

        return new StorageMonitor(
            new DiskGuard(options, disk),
            library,
            clock,
            NullLogger<StorageMonitor>.Instance);
    }

    private static RecordingSummary Summary(string id) => new(
        id,
        "channel",
        "Title",
        "Game",
        T0,
        T0.AddHours(1),
        IsRecording: false,
        DurationSeconds: 3600,
        SizeBytes: 1024,
        PartCount: 1,
        SegmentCount: 1,
        HasThumbnail: false,
        Chapters: []);
}

internal sealed class MutableDiskSpace(long freeBytes, long totalBytes) : IDiskSpace
{
    public long FreeBytes { get; set; } = freeBytes;

    public long TotalBytes { get; set; } = totalBytes;

    public long AvailableFreeSpace(string path) => FreeBytes;

    public long TotalSize(string path) => TotalBytes;
}
