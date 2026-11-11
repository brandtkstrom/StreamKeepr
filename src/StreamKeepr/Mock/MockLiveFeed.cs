using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>
/// Mock-only live feed. It cycles the seeded library through update, start, end, delete, and
/// storage changes so the drawer and shell exercise their live paths without production admin
/// endpoints. All observations go through <see cref="MockTwitch"/> and the seeded sessions.
/// </summary>
public sealed class MockLiveFeedOptions
{
    public const string SectionName = "Mock:Feed";

    /// <summary>Enable the timed feed; the E2E suite and local Mock launches use it.</summary>
    public bool Enabled { get; set; } = true;

    public int TickSeconds { get; set; } = 5;

    /// <summary>Seconds after start when a cached complete session is reopened live.</summary>
    public int StartSeconds { get; set; } = 30;

    /// <summary>Seconds after start when that session is taken offline and finalized.</summary>
    public int EndSeconds { get; set; } = 60;

    /// <summary>Seconds after start when one seeded recording is deleted.</summary>
    public int DeleteSeconds { get; set; } = 90;
}

/// <summary>
/// Deterministic mock volume: 100 GiB total, dropping 2 % free per feed tick. The real temp
/// volume barely moves during a recorded clip, which would never cross the 1 % threshold.
/// </summary>
public sealed class MockDiskSpace : IDiskSpace
{
    public const long TotalBytes = 100L * 1024 * 1024 * 1024;

    private const long StepBytes = TotalBytes / 50;
    private const long MinimumFreeBytes = TotalBytes / 20;

    private long _freeBytes = TotalBytes;

    public long AvailableFreeSpace(string path) => Interlocked.Read(ref _freeBytes);

    public long TotalSize(string path) => TotalBytes;

    /// <summary>Drops free space by 2 % of total, stopping at 5 % free.</summary>
    public void Advance()
    {
        while (true)
        {
            long current = Interlocked.Read(ref _freeBytes);
            long next = Math.Max(MinimumFreeBytes, current - StepBytes);
            if (next == current)
                return;

            if (Interlocked.CompareExchange(ref _freeBytes, next, current) == current)
                return;
        }
    }
}

/// <summary>
/// Mock-only hosted service. It re-summarizes active sessions, advances the fake volume, and
/// walks through start/end/delete milestones. It is never registered outside the Mock profile.
/// </summary>
public sealed class MockLiveFeed(
    MockTwitch twitch,
    MockDiskSpace disk,
    RecordingLibrary library,
    SessionStore sessions,
    IStorageMonitor storage,
    IOptions<StorageOptions> storageOptions,
    IOptions<MockLiveFeedOptions> feedOptions,
    TimeProvider clock,
    ILogger<MockLiveFeed> logger) : BackgroundService
{
    private const string StartLogin = "mock_echo";
    private const string DeleteLogin = "mock_echo";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MockLiveFeedOptions options = feedOptions.Value;
        if (!options.Enabled)
            return;

        TimeSpan tick = TimeSpan.FromSeconds(Math.Max(1, options.TickSeconds));
        DateTimeOffset startedAt = clock.GetUtcNow();
        bool started = false;
        bool ended = false;
        bool deleted = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(tick, clock, stoppingToken);
                TimeSpan elapsed = clock.GetUtcNow() - startedAt;

                try
                {
                    if (!started && elapsed >= TimeSpan.FromSeconds(options.StartSeconds))
                    {
                        twitch.SetLive(StartLogin);
                        started = true;
                        logger.LogInformation(
                            "Mock feed: {Channel} went live to exercise a recording start.",
                            StartLogin);
                    }

                    if (!ended && elapsed >= TimeSpan.FromSeconds(options.EndSeconds))
                    {
                        twitch.SetOffline(StartLogin);
                        ended = true;
                        logger.LogInformation(
                            "Mock feed: {Channel} went offline to exercise a recording end.",
                            StartLogin);
                    }

                    if (!deleted && elapsed >= TimeSpan.FromSeconds(options.DeleteSeconds))
                    {
                        DeleteSeededRecording();
                        deleted = true;
                    }

                    await RefreshActiveSummariesAsync(stoppingToken);
                    disk.Advance();
                    storage.Refresh();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException or FormatException or NotSupportedException)
                {
                    // A mock session can be mid-write; keep the feed alive and retry next tick.
                    logger.LogWarning(ex, "Mock feed step failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task RefreshActiveSummariesAsync(CancellationToken ct)
    {
        string root = Path.GetFullPath(storageOptions.Value.RecordingsRoot);

        foreach (RecordingSummary summary in library.All)
        {
            if (!summary.IsRecording)
                continue;

            string directory = Path.Combine(root, summary.Channel, summary.Id);
            try
            {
                if (await sessions.SummarizeAsync(directory, ct) is { } updated)
                    library.Upsert(updated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or FormatException or NotSupportedException)
            {
                // The recorder can be mid-write; the next tick retries.
                logger.LogDebug(
                    ex, "Mock feed could not summarize the active session at {Directory}.", directory);
            }
        }
    }

    private void DeleteSeededRecording()
    {
        MockRecording? target = MockCatalog.Recordings
            .Where(recording => recording.Complete && recording.Channel == DeleteLogin)
            .OrderBy(recording => recording.StartedAt)
            .FirstOrDefault();

        if (target is null)
            return;

        string root = Path.GetFullPath(storageOptions.Value.RecordingsRoot);
        var session = MockCatalog.SessionFor(target, root);

        library.Remove(session.Id);
        if (Directory.Exists(session.Directory))
            Directory.Delete(session.Directory, recursive: true);

        storage.Refresh();
        logger.LogInformation(
            "Mock feed: deleted the seeded recording {SessionId} to exercise a delete.",
            session.Id);
    }
}
