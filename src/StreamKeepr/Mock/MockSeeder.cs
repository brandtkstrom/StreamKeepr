using System.Collections.Immutable;
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
}

/// <summary>
/// Populates the disposable Mock recordings root before startup recovery runs: four fake
/// channels and eight sessions, including a finished VOD, a complete two-part session, a
/// two-part partial, a growing EVENT capture, thumbnails, and chapters. All IDs and timestamps
/// come from <see cref="MockCatalog"/>.
/// </summary>
public sealed class MockSeeder(
    IOptions<StorageOptions> storage,
    IOptions<RecorderOptions> recorder,
    IOptions<MockSeedOptions> seedOptions,
    ChannelStore channels,
    SessionStore sessions,
    RecordingLibrary library,
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
            logger.LogInformation("Keeping the existing mock recordings at {Root}.", root);
            return;
        }

        await SeedChannelsAsync(cancellationToken);
        await SeedRecordingsAsync(root, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, SeedVersion, cancellationToken);

        logger.LogInformation(
            "Seeded {RecordingCount} recordings for {ChannelCount} mock channels at {Root}.",
            MockCatalog.Recordings.Count,
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

    private async Task SeedRecordingsAsync(string root, CancellationToken ct)
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
                await SeedRecordingAsync(recording, root, template, templatePlaylist, ct);
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
        CancellationToken ct)
    {
        Session session = MockCatalog.SessionFor(recording, root);
        Directory.CreateDirectory(session.Directory);

        for (int part = 1; part <= recording.Parts; part++)
        {
            MockMedia.CopyPartFiles(template, session.Directory, part);
            string playlist = MockMedia.RenderPartPlaylist(
                templatePlaylist,
                part,
                session.StartedAt + (MockMedia.PartDuration * (part - 1)));
            await File.WriteAllTextAsync(
                Path.Combine(session.Directory, $"p{part:D3}.m3u8"), playlist, ct);
        }

        File.Copy(
            Path.Combine(template, MockMedia.Thumbnail),
            Path.Combine(session.Directory, SessionStore.ThumbnailFileName),
            overwrite: true);

        DateTimeOffset? endedAt = recording.Complete
            ? session.StartedAt + (MockMedia.PartDuration * recording.Parts)
            : null;

        var manifest = new SessionManifest(
            SessionStore.ManifestVersion,
            session.Id,
            session.Channel,
            session.StreamId,
            session.StartedAt,
            recording.Complete ? SessionStatus.Complete : SessionStatus.Recording,
            ImmutableList.CreateRange(MockCatalog.MetadataFor(recording)),
            EndedAt: endedAt);

        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, SessionStore.ManifestFileName),
            SessionStore.SerializeManifest(manifest),
            ct);

        if (recording.Complete)
            await sessions.FinalizeAsync(session, ct);
        else
            library.Upsert((await sessions.SummarizeAsync(session.Directory, ct))!);
    }
}
