using System.Security.Claims;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Administration of finished recordings (backend-for-UI contract §6).</summary>
public interface IRecordingAdmin
{
    /// <summary>Permanently deletes a complete recording and its folder.</summary>
    Task DeleteAsync(string recordingId, ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>
/// Deletes one complete session: checks authorization and the export lease, holds the session
/// lock, re-reads the manifest, then removes the library entry, folder, and watch progress.
/// </summary>
public sealed class RecordingAdmin(
    RecordingLibrary library,
    SessionStore sessions,
    IProgressStore progress,
    IExportLeaseRegistry leases,
    IStorageMonitor storage,
    IOptions<StorageOptions> storageOptions,
    ILogger<RecordingAdmin> logger) : IRecordingAdmin
{
    public async Task DeleteAsync(
        string recordingId,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Connect Twitch to delete recordings.");

        RecordingSummary summary = library.Find(recordingId)
            ?? throw new KeyNotFoundException("That recording is not in the library.");

        if (!Session.IsValidId(summary.Id) ||
            !RecordingsPath.TryResolve(
                storageOptions.Value.RecordingsRoot,
                [summary.Channel, summary.Id],
                out string directory))
        {
            throw new InvalidOperationException("That recording cannot be deleted.");
        }

        // The guard closes the check-then-delete race with ExportService.Start: once it is
        // held, no new export can acquire the recording until the folder is gone.
        using IDisposable delete = leases.TryBeginDelete(recordingId)
            ?? throw new InvalidOperationException(
                "That recording is being deleted or exported.");

        SemaphoreSlim gate = sessions.LockFor(directory);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        bool deleted = false;
        try
        {
            SessionManifest? manifest = await sessions.TryLoadAsync(directory, ct).ConfigureAwait(false);
            if (manifest is null || !string.Equals(manifest.Id, recordingId, StringComparison.Ordinal))
                throw new KeyNotFoundException("That recording was already deleted.");

            if (!string.Equals(manifest.Channel, summary.Channel, StringComparison.Ordinal))
                throw new InvalidOperationException("That recording cannot be deleted.");

            if (manifest.Status != SessionStatus.Complete)
                throw new InvalidOperationException("That recording is still being captured.");

            library.Remove(recordingId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);

            await progress.ClearAsync(recordingId, ct).ConfigureAwait(false);
            storage.Refresh();
            deleted = true;
            logger.LogInformation(
                "Deleted recording {RecordingId} from {Directory}.", recordingId, directory);
        }
        finally
        {
            gate.Release();
        }

        if (deleted)
            sessions.ForgetLock(directory);
    }
}
