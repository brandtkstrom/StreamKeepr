using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>
/// Seeds deterministic viewer samples across every finished Mock recording so the Stats viewer
/// chart has data. It runs after <see cref="MockSeeder"/> has recorded the sessions; live capture
/// fills the growing session at its own cadence. A recording that already has samples (a kept
/// root) is left alone.
/// </summary>
public sealed class MockViewerSeeder(
    IOptions<StorageOptions> storage,
    IOptions<MockSeedOptions> seedOptions,
    RecordingLibrary library,
    ILogger<MockViewerSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!seedOptions.Value.Enabled)
            return;

        string root = MockEnvironment.ResolveRecordingsRoot(
            storage.Value.RecordingsRoot, Path.GetTempPath());
        int seeded = 0;

        MockSeedOptions options = seedOptions.Value;
        foreach (MockRecording recording in MockCatalog.Recordings.Concat(
            MockCatalog.ArchiveRecordings(options.ScaleRecordings)))
        {
            Session session = MockCatalog.SessionFor(recording, root);
            string samples = Path.Combine(session.Directory, ViewerSamples.FileName);

            // Only finished recordings that MockSeeder recorded, and never over kept samples.
            if (library.Find(session.Id) is not { IsRecording: false } summary
                || !Directory.Exists(session.Directory)
                || File.Exists(samples))
            {
                continue;
            }

            double duration = summary.DurationSeconds ?? 0;
            double spacing = MockCatalog.SeededViewerSpacing.TotalSeconds;
            IEnumerable<string> lines = Enumerable.Range(0, (int)(duration / spacing) + 1)
                .Select(i => i * spacing)
                .Select(at => ViewerSamples.FormatLine(at, MockCatalog.ViewerCount(session.StreamId, at)));

            await File.WriteAllLinesAsync(samples, lines, cancellationToken);
            seeded++;
        }

        logger.LogInformation("Seeded viewer samples for {RecordingCount} mock recordings.", seeded);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
