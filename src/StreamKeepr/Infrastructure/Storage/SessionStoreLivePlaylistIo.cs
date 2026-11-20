using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Builds a live playlist through the session store's existing stitch path. In #241 the
/// refresher still shares the session manifest lock; #242 separates that lock and #243 makes
/// the build incremental.
/// </summary>
public sealed class SessionStoreLivePlaylistIo(
    SessionStore store,
    IOptions<StorageOptions> storage) : ILivePlaylistIo
{
    public async Task<LivePlaylistRead> ReadAsync(
        RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!RecordingsPath.TryResolve(
                storage.Value.RecordingsRoot, [summary.Channel, summary.Id], out string directory))
        {
            return LivePlaylistRead.Missing;
        }

        if (!File.Exists(Path.Combine(directory, SessionStore.ManifestFileName)))
            return LivePlaylistRead.Missing;

        SessionManifest? manifest = await store.TryLoadAsync(directory, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            // The manifest exists but could not be read. Treat it as a refresh failure so the
            // last-known-good body stays live instead of being pruned as a deleted session.
            throw new InvalidDataException(
                $"The session manifest for {summary.Id} is unreadable.");
        }

        string? body = await store.StitchLiveAsync(directory, ct).ConfigureAwait(false);
        if (body is null)
            return LivePlaylistRead.Missing;

        return manifest.Status == SessionStatus.Complete
            ? LivePlaylistRead.Complete(body)
            : LivePlaylistRead.Live(body);
    }
}
