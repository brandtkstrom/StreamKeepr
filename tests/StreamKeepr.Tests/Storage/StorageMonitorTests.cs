using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
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
    public void Samples_every_sixty_seconds_and_updates_current_for_sub_threshold_moves()
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
        Assert.Equal(490, monitor.Current.FreeBytes);

        disk.FreeBytes = 489; // just over 1 % from the last notified sample
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, changed);
        Assert.Equal(489, monitor.Current.FreeBytes);

        disk.FreeBytes = 480; // under 1 % from the new sample
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, changed);
        Assert.Equal(480, monitor.Current.FreeBytes);
    }

    [Fact]
    public async Task Concurrent_refreshes_sample_and_commit_under_the_same_lock()
    {
        var disk = new CoordinatedDiskSpace(freeBytes: 500, totalBytes: 1000);
        using StorageMonitor monitor = Create(disk, out _, out _);
        int changed = 0;
        monitor.Changed += () => changed++;

        disk.BlockNextSample();
        Task first = Task.Run(monitor.Refresh);
        Assert.True(disk.WaitUntilBlocked());

        disk.FreeBytes = 400;
        Task second = Task.Run(monitor.Refresh);
        disk.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(400, monitor.Current.FreeBytes);
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

        disk.FreeBytes = 490; // under 1 % of the new total
        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(1, changed);
        Assert.Equal(490, monitor.Current.FreeBytes);
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

    [Fact]
    public void Crossing_the_free_space_minimum_logs_once_per_transition()
    {
        const long Gib = DiskGuard.BytesPerGib;
        var disk = new MutableDiskSpace(freeBytes: 20 * Gib, totalBytes: 100 * Gib);
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        using StorageMonitor monitor = Create(
            disk, out FakeTimeProvider clock, out _, minFreeGb: 10,
            loggerFactory.CreateLogger<StorageMonitor>());

        Assert.Empty(DiskLogs(logs));

        disk.FreeBytes = 9 * Gib + Gib / 2; // 9.5 GiB: below the 10 GiB minimum
        clock.Advance(StorageMonitor.SampleInterval);

        CapturedLogEntry low = Assert.Single(DiskLogs(logs));
        Assert.Equal(LogLevel.Warning, low.Level);
        Assert.Equal(9.5, low.Properties["FreeGb"]);
        Assert.Equal(10, low.Properties["MinFreeGb"]);
        Assert.Equal(100.0, low.Properties["TotalGb"]);

        disk.FreeBytes = 5 * Gib; // still low: the sample changes, the state does not
        clock.Advance(StorageMonitor.SampleInterval);
        monitor.Refresh();

        Assert.Single(DiskLogs(logs));

        disk.FreeBytes = 10 * Gib; // exactly the minimum has room again, as DiskGuard decides
        clock.Advance(StorageMonitor.SampleInterval);

        Assert.Equal(2, DiskLogs(logs).Count);
        CapturedLogEntry recovered = DiskLogs(logs)[1];
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Equal(10.0, recovered.Properties["FreeGb"]);
        Assert.Equal(10, recovered.Properties["MinFreeGb"]);
        Assert.Equal(100.0, recovered.Properties["TotalGb"]);

        disk.FreeBytes = 50 * Gib;
        clock.Advance(StorageMonitor.SampleInterval);

        Assert.Equal(2, DiskLogs(logs).Count);
    }

    [Fact]
    public void Starting_below_the_free_space_minimum_logs_the_warning_once()
    {
        const long Gib = DiskGuard.BytesPerGib;
        var disk = new MutableDiskSpace(freeBytes: 2 * Gib, totalBytes: 100 * Gib);
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggerFactory = TestLogging.Factory(logs);
        using StorageMonitor monitor = Create(
            disk, out FakeTimeProvider clock, out _, minFreeGb: 10,
            loggerFactory.CreateLogger<StorageMonitor>());

        clock.Advance(StorageMonitor.SampleInterval);

        CapturedLogEntry low = Assert.Single(DiskLogs(logs));
        Assert.Equal(LogLevel.Warning, low.Level);
        Assert.Equal(2.0, low.Properties["FreeGb"]);
    }

    private static List<CapturedLogEntry> DiskLogs(ListLoggerProvider logs) =>
        logs.Entries.Where(entry => entry.Properties.ContainsKey("MinFreeGb")).ToList();

    private static StorageMonitor Create(
        IDiskSpace disk,
        out FakeTimeProvider clock,
        out RecordingLibrary library,
        int minFreeGb = 1,
        ILogger<StorageMonitor>? logger = null)
    {
        var options = Options.Create(new StorageOptions
        {
            RecordingsRoot = Path.GetTempPath(),
            MinFreeGb = minFreeGb,
        });

        clock = new FakeTimeProvider(T0);
        library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);

        return new StorageMonitor(
            new DiskGuard(options, disk),
            library,
            clock,
            logger ?? NullLogger<StorageMonitor>.Instance);
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

internal sealed class CoordinatedDiskSpace : IDiskSpace
{
    private readonly ManualResetEventSlim _blocked = new(false);
    private readonly ManualResetEventSlim _released = new(false);
    private long _freeBytes;
    private long _totalBytes;
    private int _blockNext;

    public CoordinatedDiskSpace(long freeBytes, long totalBytes)
    {
        _freeBytes = freeBytes;
        _totalBytes = totalBytes;
    }

    public long FreeBytes
    {
        get => Interlocked.Read(ref _freeBytes);
        set => Interlocked.Exchange(ref _freeBytes, value);
    }

    public void BlockNextSample() => Interlocked.Exchange(ref _blockNext, 1);

    public bool WaitUntilBlocked() => _blocked.Wait(TimeSpan.FromSeconds(5));

    public void Release() => _released.Set();

    public long AvailableFreeSpace(string path)
    {
        long value = FreeBytes;
        if (Interlocked.Exchange(ref _blockNext, 0) == 1)
        {
            _blocked.Set();
            _released.Wait(TimeSpan.FromSeconds(5));
        }

        return value;
    }

    public long TotalSize(string path) => Interlocked.Read(ref _totalBytes);
}
