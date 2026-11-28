using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>Mock-only seeding controls.</summary>
public sealed class MockSeedOptions
{
    public const string SectionName = "Mock:Seed";

    /// <summary>Disable seeding for a host that only needs the fake services.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Delete and recreate the disposable root on every start. E2E uses a unique temp root and
    /// relies on this for deterministic journeys.
    /// </summary>
    public bool ResetOnStart { get; set; } = true;

    /// <summary>
    /// Extra finished archive recordings for measuring the library at scale (#110). They have
    /// a playlist, manifest, and thumbnail but no media segments, so they list but do not play.
    /// </summary>
    [Range(0, 5000)]
    public int ScaleRecordings { get; set; }
}

/// <summary>
/// Populates the disposable Mock recordings root and the session records before startup
/// recovery runs: four fake channels and eight sessions, including a finished VOD, a complete
/// two-part session, a two-part partial, a growing EVENT capture, thumbnails, and chapters. All
/// IDs and timestamps come from <see cref="MockCatalog"/>.
/// <see cref="MockSeedOptions.ScaleRecordings"/> adds media-less archive recordings for
/// measuring the library at scale.
/// </summary>
/// <remarks>
/// Seeding is split by store so each can change on its own: this seeder owns channels and
/// recordings (the session store); <see cref="MockViewerSeeder"/> owns viewer samples and
/// <see cref="MockChatSeeder"/> owns chat, both run after it.
/// </remarks>
public sealed class MockSeeder(
    IOptions<StorageOptions> storage,
    IOptions<RecorderOptions> recorder,
    IOptions<MockSeedOptions> seedOptions,
    ChannelStore channels,
    SessionStore sessions,
    ILogger<MockSeeder> logger) : IHostedService
{
    private const string SeedVersion = "1";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        MockSeedOptions options = seedOptions.Value;
        if (!options.Enabled)
            return;

        string root = MockEnvironment.ResolveRecordingsRoot(
            storage.Value.RecordingsRoot, Path.GetTempPath());
        string marker = Path.Combine(
            AppDataPaths.AppDataDirectory(root), "mock-seeded");

        if (options.ResetOnStart && Directory.Exists(root))
        {
            // Only delete a root this seeder created or an empty temp directory. Preflight
            // creates the app's own .streamkeepr directory before this hosted service runs, so
            // a root containing only that directory is still safe; anything else is unrelated
            // data and must never be deleted.
            string appData = Path.GetFullPath(AppDataPaths.AppDataDirectory(root));
            bool owned = File.Exists(marker)
                || Directory.EnumerateFileSystemEntries(root).All(entry =>
                    string.Equals(
                        Path.GetFullPath(entry),
                        appData,
                        StringComparison.Ordinal));
            if (!owned)
            {
                throw new InvalidOperationException(
                    $"Refusing to reset '{root}': it contains data unrelated to the Mock " +
                    "profile and has no mock-seeded marker. Choose an empty temp root or set " +
                    "Mock:Seed:ResetOnStart=false.");
            }

            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);

        if (!options.ResetOnStart && File.Exists(marker))
        {
            // The Mock record store is per process, so the channel list and session records
            // start empty every time; record the kept recordings again without new media.
            await SeedChannelsAsync(cancellationToken);
            await RecordKeptRecordingsAsync(root, options.ScaleRecordings, cancellationToken);
            logger.LogInformation("Keeping the existing mock recordings at {Root}.", root);
            return;
        }

        await SeedChannelsAsync(cancellationToken);
        await SeedRecordingsAsync(root, options.ScaleRecordings, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, SeedVersion, cancellationToken);

        logger.LogInformation(
            "Seeded {RecordingCount} recordings for {ChannelCount} mock channels at {Root}.",
            MockCatalog.Recordings.Count + options.ScaleRecordings,
            MockCatalog.Streamers.Count,
            root);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedChannelsAsync(CancellationToken ct)
    {
        await channels.LoadAsync(ct);

        foreach (MockStreamer streamer in MockCatalog.Streamers)
        {
            if (channels.Snapshot.Any(entry => entry.Login == streamer.Login))
                continue;

            await channels.AddAsync(
                new ChannelEntry(
                    streamer.Login,
                    Paused: false,
                    UserId: streamer.UserId,
                    DisplayName: streamer.DisplayName,
                    AvatarUrl: null),
                ct);
        }
    }

    private async Task SeedRecordingsAsync(string root, int scaleRecordings, CancellationToken ct)
    {
        string template = Path.Combine(
            AppDataPaths.AppDataDirectory(root), "mock-template");
        Directory.CreateDirectory(template);

        try
        {
            await MockMedia.GenerateTemplateAsync(recorder.Value.FfmpegPath, template, ct);
            string templatePlaylist = await File.ReadAllTextAsync(
                Path.Combine(template, MockMedia.TemplatePlaylist), ct);

            foreach (MockRecording recording in MockCatalog.Recordings)
                await SeedRecordingAsync(recording, root, template, templatePlaylist, withMedia: true, ct);

            foreach (MockRecording recording in MockCatalog.ArchiveRecordings(scaleRecordings))
                await SeedRecordingAsync(recording, root, template, templatePlaylist, withMedia: false, ct);
        }
        finally
        {
            if (Directory.Exists(template))
                Directory.Delete(template, recursive: true);
        }
    }

    private async Task SeedRecordingAsync(
        MockRecording recording,
        string root,
        string template,
        string templatePlaylist,
        bool withMedia,
        CancellationToken ct)
    {
        Session session = MockCatalog.SessionFor(recording, root);
        Directory.CreateDirectory(session.Directory);

        for (int part = 1; part <= recording.Parts; part++)
        {
            if (withMedia)
                MockMedia.CopyPartFiles(template, session.Directory, part);
            string playlist = MockMedia.RenderPartPlaylist(
                templatePlaylist,
                part,
                session.StartedAt + (MockMedia.PartDuration * (part - 1)));
            await File.WriteAllTextAsync(
                Path.Combine(session.Directory, $"p{part:D3}.m3u8"), playlist, ct);
        }

        // The thumbnail goes through the store, which records it; the image is consumed.
        string image = SeedImagePath(session);
        File.Copy(Path.Combine(template, MockMedia.Thumbnail), image, overwrite: true);

        await RecordAsync(recording, session, image, ct);
    }

    /// <summary>
    /// Records every catalog recording whose folder survived from an earlier run, as the seed
    /// did, keeping its media and thumbnail.
    /// </summary>
    private async Task RecordKeptRecordingsAsync(string root, int scaleRecordings, CancellationToken ct)
    {
        foreach (MockRecording recording in MockCatalog.Recordings.Concat(
            MockCatalog.ArchiveRecordings(scaleRecordings)))
        {
            Session session = MockCatalog.SessionFor(recording, root);
            if (!Directory.Exists(session.Directory))
                continue;

            // Saving over an existing thumbnail records it and consumes only the copy.
            string thumbnail = Path.Combine(session.Directory, SessionStore.ThumbnailFileName);
            string? image = null;
            if (File.Exists(thumbnail))
            {
                image = SeedImagePath(session);
                File.Copy(thumbnail, image, overwrite: true);
            }

            await RecordAsync(recording, session, image, ct);
        }
    }

    private async Task RecordAsync(
        MockRecording recording, Session session, string? thumbnailImage, CancellationToken ct)
    {
        DateTimeOffset? endedAt = recording.Complete
            ? session.StartedAt + (MockMedia.PartDuration * recording.Parts)
            : null;

        await sessions.SeedAsync(session, [.. MockCatalog.MetadataFor(recording)], endedAt, ct);
        if (thumbnailImage is not null)
            await sessions.SaveThumbnailAsync(session.Directory, thumbnailImage, ct);
        if (recording.Complete)
            await sessions.FinalizeAsync(session, ct);
    }

    /// <summary>Where a thumbnail image waits, on the recordings volume, for the store to take it.</summary>
    private static string SeedImagePath(Session session) =>
        Path.Combine(session.Directory, SessionStore.ThumbnailFileName + ".seed");
}
