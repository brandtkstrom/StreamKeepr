using System.Security.Claims;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Administration of finished recordings (backend-for-UI contract §6).</summary>
public interface IRecordingAdmin
{
    /// <summary>Permanently deletes a complete recording and its folder.</summary>
    Task DeleteAsync(string recordingId, ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>
/// Deletes one complete session: checks authorization and the export lease, holds the session
/// lock, re-reads the session record, then removes the library entry, the record with every
/// row that cascades from it, the folder (best effort), and the cached watch progress. Export
/// files live outside the session folder and are kept.
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
            SessionRecord? record = await sessions.TryLoadAsync(directory, ct).ConfigureAwait(false);
            if (record is null || !string.Equals(record.Id, recordingId, StringComparison.Ordinal))
                throw new KeyNotFoundException("That recording was already deleted.");

            if (!string.Equals(record.Channel, summary.Channel, StringComparison.Ordinal))
                throw new InvalidOperationException("That recording cannot be deleted.");

            if (record.Status != SessionStatus.Complete)
                throw new InvalidOperationException("That recording is still being captured.");

            // The library entry goes first so reads return 404 at once. The record goes before
            // the folder so a deleted recording never returns on restart: the library is built
            // from the record store only, and a folder without a record is never read.
            // Removing the entry also drops the resume position, so keep it for a rollback.
            double? resumePosition = progress.Get(recordingId);
            library.Remove(recordingId);
            try
            {
                await sessions.DeleteRecordAsync(recordingId, ct).ConfigureAwait(false);
            }
            catch
            {
                // Nothing was deleted: list the recording again rather than hide it until restart.
                library.Upsert(summary);
                await RestoreResumePositionAsync(recordingId, resumePosition).ConfigureAwait(false);
                throw;
            }

            // The delete is committed: finish the cleanup even if the caller cancels now.
            DeleteFolder(recordingId, directory);
            await progress.ClearAsync(recordingId, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Best effort, during a rollback: a failure is logged so the caller still sees why the
    /// delete failed. The viewer then starts from the beginning, which loses nothing else.
    /// </summary>
    private async Task RestoreResumePositionAsync(string recordingId, double? position)
    {
        if (position is not double seconds)
            return;

        try
        {
            await progress.SetAsync(recordingId, seconds, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (RecordStoreFailures.IsStoreFailure(ex))
        {
            logger.LogWarning(ex,
                "Could not restore the resume position of recording {RecordingId} after its delete failed.",
                recordingId);
        }
    }

    /// <summary>
    /// Best effort: the recording is already deleted once its record is gone, so a folder that
    /// cannot be removed is logged as an orphan for the operator, never surfaced as a failure.
    /// </summary>
    private void DeleteFolder(string recordingId, string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Deleted recording {RecordingId}, but its folder {Directory} could not be removed; it is an orphan that the library never lists. Remove it by hand.",
                recordingId, directory);
        }
    }
}
