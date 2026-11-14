# Stream Keepr Session Store Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 5; `docs/domain-model.md`; `docs/recorder-contract.md`; plan review A-02/A-03/A-08/A-09.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope and boundary

The session store owns the on-disk session manifest, part stitching, startup recovery, the in-memory recording library, and disk-space checks. The recorder owns part files; the store owns `session.json`, `index.m3u8`, `thumb.jpg`, and library summaries. The store never runs Streamlink/FFmpeg and never changes domain state. A session folder may also contain a `chat/` sidecar owned by `ChatLogStore`; the session store only derives `HasChat` and deletes the folder with the session.

## 2. Manifest

```csharp
using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

public enum SessionStatus { Recording, Complete }

public sealed record MetadataChange(DateTimeOffset At, string Title, string Game);

public sealed record Chapter(double StartSeconds, string Title, string Game);

public sealed record SessionManifest(
    int Version,
    string Id,
    string Channel,
    string StreamId,
    DateTimeOffset StartedAt,
    SessionStatus Status,
    ImmutableList<MetadataChange> Metadata,   // first = title at start; later = changes
    DateTimeOffset? EndedAt = null,
    double? DurationSeconds = null,
    long? SizeBytes = null);
```

`Version` is 1. `Status` has only `Recording` and `Complete`; the UI's `Reconnecting`/`Partial` states are derived, not stored.

### Derived summary fields

`RecordingSummary` adds `PartCount`, `SegmentCount`, and `EndedAt`. Derive, do not store:

- `Title` and `Game`: latest `Metadata` entry.
- `PartCount`: number of non-empty `pNNN.m3u8` files.
- `SegmentCount`: number of segment URI lines across the part playlists (or the final index).
- `DurationSeconds`: `PlaylistStitcher.TotalDuration` of the final index; null while recording.
- `SizeBytes`: sum of `init_*.mp4`, `*.m4s`, and `index.m3u8`; excludes `session.json`, `thumb.jpg`, and part playlists.
- `EndedAt`: manifest value; null while recording.
- `HasThumbnail`: `thumb.jpg` exists.
- `Chapters`: metadata changes after the first, positioned against the stitched playlist.
- `HasChat`: `chat/meta.json` exists or at least one `chat/*.jsonl` bucket exists.
- `IsRecording`: `Status == Recording`.

`SizeBytes` excludes `chat/`; the chat log has its own `Chat:MaxBytesPerSession` cap.

## 3. Atomic writes

- Manifest, stitched index, and thumbnail are written to `<name>.tmp` in the same directory, then renamed over the target with `File.Move(temp, target, overwrite: true)`.
- Atomicity relies on a local POSIX filesystem (the Docker volume). Network filesystems are not supported.
- A failed rename leaves the previous complete file and a `.tmp`; recovery deletes stale `.tmp` files.
- No fsync is required. A host power loss may lose the last metadata update; media files remain the source of truth.
- Thumbnail writes are best-effort and atomic. If the write fails, log it; `HasThumbnail` stays false and the library may update when the write completes.

## 4. Locking

`LockFor(sessionDir)` returns the per-session `SemaphoreSlim`.

| Operation | Lock | Notes |
|---|---|---|
| `OpenAsync` | yes | Takes the lock once, then calls core open/reopen. Never nests a locking `ReopenAsync`. |
| `ReopenAsync` | yes | Public entry; internal `ReopenCoreAsync` assumes the lock. |
| `AppendMetadataAsync` | yes | Read-modify-write is atomic under the lock. |
| `FinalizeAsync` | yes | Writes index and manifest, updates library, invalidates the live cache. |
| `DeleteAsync` | yes | Removes the library entry first; deletes files; removes the live cache and lock entry. |
| Recovery finalize | yes | Calls `FinalizeAsync`. |
| `StitchLiveAsync` | cache miss only | A cache hit is lock-free. A cache miss takes the lock, reads part playlists, and rebuilds the cache. |
| `TryLoadAsync` | startup only | Recovery runs before channel loops; no contention. |
| Thumbnail write | no | Atomic, best-effort; does not read or modify the manifest. |

Rules:

- Hold at most one session lock at a time. Never acquire a second session lock while holding one.
- Keep the lock entry for finalized sessions because they may reopen; remove it on delete.
- `StitchLiveAsync` reads the manifest under the lock. If the status is `Complete`, return the finalized `index.m3u8` instead of a live stitch.
- Delete removes the library entry before taking the lock, so new requests return 404; an in-flight request may also return 404/410, never a 500.

## 5. Stitching

`PlaylistStitcher.Stitch(partPlaylists, minTargetDuration, final)`:

- Header: `#EXTM3U`, `#EXT-X-VERSION:7`, `#EXT-X-TARGETDURATION`, `#EXT-X-MEDIA-SEQUENCE:0`, `#EXT-X-PLAYLIST-TYPE:EVENT`, `#EXT-X-INDEPENDENT-SEGMENTS`.
- For each part in ascending numeric part order, strip the part header tags (`#EXTM3U`, version, target duration, media sequence, playlist type, independent segments, ENDLIST) and keep the body, including the part's `#EXT-X-MAP` and `#EXT-X-PROGRAM-DATE-TIME`.
- Insert `#EXT-X-DISCONTINUITY` between non-empty parts.
- Keep every line through the last segment URI; drop a trailing `#EXTINF` without a URI.
- Append `#EXT-X-ENDLIST` only when `final` is true.
- Use `\n` line endings and invariant formatting.

Target duration:

- Live and final target = `max(2 * SegmentSeconds, max part target)`.
- A later part with longer segments (for example one spanning an ad break) can raise the live target. **Accepted risk:** hls.js tolerates the change; the finalized index is stable. Revisit if a strict client appears or the drift prevents FFmpeg finalization.
- Part numbers are 1–999. The `p{part:D3}` format and ordinal sort rely on this. Part 1000 is a hard error; the session finalizes.

Chapters:

- Skip the initial metadata entry; chapters begin at the first title/game change.
- Place each change at the start of the segment where `change.At <= segmentStart + segmentDuration`; offset is cumulative duration.
- A null or unparseable playlist yields no chapters.

Live cache:

- Key: the last part playlist path, its last-write time, and its length.
- Cache hit: return the cached body without the lock.
- Cache miss: take the lock, read the part playlists, stitch with `final: false`, cache the body.
- Invalidate on finalize, reopen, delete, and any part-playlist change (the key covers the latter).
- If the manifest is `Complete`, serve the finalized `index.m3u8`; never a live stitch without ENDLIST.

## 6. Recovery

Recovery runs once at startup, before any channel coordinator processes events. No recorder child may survive concurrently; the container teardown guarantees this.

For each `recordingsRoot/{channel}/{session}`:

1. Skip dot-prefixed directories at both levels and `.streamkeepr`.
2. If `session.json` is missing: log and skip; leave media untouched.
3. Read the manifest. If corrupt or empty: log and skip; leave media untouched.
4. If `Status == Complete`:
   - If `index.m3u8` exists and parses, add `Resumable(Session, LastPart + 1, Complete: true)` to finished.
   - If the index is missing but part playlists exist, re-finalize (stitch, atomic index, manifest complete, library upsert), then add to finished.
   - If there are no parts, log and skip; do not serve.
5. If `Status == Recording`:
   - Delete stale `*.tmp` files.
   - Ignore `chat/`; chat recovery is independent and append-only.
   - Parse every part playlist. If any parse fails, log and skip the session; leave media untouched.
   - Delete `*.m4s` and `init_*.mp4` not referenced by any part playlist.
   - Add `Resumable(Session, LastPart + 1, Complete: false)` to unfinished.
6. Per watched channel:
   - For watched, unpaused channels, keep the newest unfinished session as `Unfinished`.
   - Finalize every other unfinished session (older, paused, or removed) and add it to finished.
   - Keep the newest finished session as `Finished`.
7. Return `RecoveredChannel(Unfinished, Finished)` per watched channel.
8. Call `RecordingLibrary.MarkLoaded()` after all sessions are loaded.

The supervisor maps `Unfinished` to `Idle.Resume` and `Finished` to `Idle.Finished`; a paused channel starts as `Paused(Finished)` rather than resuming. `LastPart` parses the numeric part from `pNNN.m3u8`.

## 7. Session operations

- `OpenAsync(session, live)`: under the lock, if `session.json` exists, reopen; otherwise create the directory, write manifest v1 with the first metadata entry, upsert the library, and start the best-effort thumbnail write.
- `ReopenAsync(session)`: under the lock, set `Status = Recording`, clear `EndedAt`/`DurationSeconds`/`SizeBytes`, delete `index.m3u8`, write the manifest, upsert the library, invalidate the live cache.
- `AppendMetadataAsync(session, live)`: under the lock, read the manifest; if the latest title and game are unchanged, return after ensuring a thumbnail exists. Otherwise append a metadata change, write the manifest, upsert the library with live counts and chapters.
- `FinalizeAsync(session)`: under the lock, collect non-empty part playlists in numeric order. If none, delete the session directory and remove the library entry. Otherwise stitch final, write `index.m3u8` atomically, set `Status = Complete`, `EndedAt`, `DurationSeconds`, `SizeBytes`, write the manifest, upsert the library with counts/chapters, and remove the live cache.
- `DeleteAsync(session)`: remove the library entry first, take the lock, delete the directory, remove the live cache and lock entry. Export leases are checked by the caller. The session directory includes `chat/`, so chat is deleted with the recording. `FinalizeAsync` does not rewrite chat; the chat writer closes and optionally purges deleted text on its own finalize path.
- `StitchLiveAsync(dir)`: cache hit or lock + rebuild as above.

## 8. Recording library

`RecordingSummary`:

```csharp
public sealed record RecordingSummary(
    string Id, string Channel, string Title, string Game, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, bool IsRecording, double? DurationSeconds, long? SizeBytes,
    int PartCount, int SegmentCount, bool HasThumbnail,
    IReadOnlyList<Chapter> Chapters)
{
    public string PlaylistUrl => $"/media/{Channel}/{Id}/index.m3u8";
    public string? ThumbnailUrl => HasThumbnail ? $"/media/{Channel}/{Id}/thumb.jpg" : null;
}
```

- `RecordingLibrary` is an in-memory index; nothing scans the disk per page load.
- `All` is newest first by `StartedAt`; `Find(id)` is O(1).
- `Upsert`, `Remove`, and `MarkLoaded` update the immutable dictionary, refresh the sorted list, and raise `Changed`.
- `IsLoaded` is false until recovery completes; the UI shows skeletons until then.
- `Changed` may fire concurrently; subscribers marshal to their renderer. `LibraryView` is responsible for the 500 ms throttle.
- `MarkLoaded` runs after every session has been loaded, including skipped/corrupt ones.

## 9. Disk guard and retention

- `DiskGuard.FreeBytes` reads `DriveInfo.AvailableFreeSpace` for `RecordingsRoot`; the root must exist (the host creates it).
- `HasRoomToRecord()` is `FreeBytes >= MinFreeGb * 1 GiB`.
- The coordinator checks it before every part; a full disk ends the part, then the normal retry/cooldown path applies.
- **Retention is a non-goal** (`ADR-0004`). The spec omits `RetentionDays`/`MaxTotalGb` and any hourly service. Recordings are kept until manually deleted through the recording admin.

## 10. Failure modes

| Scenario | Behaviour |
|---|---|
| Crash during manifest write | `.tmp` remains; old manifest intact; recovery deletes `.tmp`. |
| Crash during finalize | Index may be `.tmp`; manifest may still say Recording; recovery re-finalizes. |
| Crash during reopen | Index deleted, manifest Recording; recovery resumes at `LastPart + 1`. |
| Corrupt/missing manifest | Log and skip; never delete media. |
| Unparseable part playlist | Log and skip the session; never delete media. |
| Complete session with missing index | Re-finalize from parts; if no parts, log and skip. |
| Missing segment referenced by a playlist | Log; do not rewrite the playlist; playback may 404. |
| Stitch races finalize | Lock/cache; a Complete manifest serves the finalized index. |
| Delete races stitch | Library entry removed first; in-flight requests return 404/410. |
| Delete races export | Export lease refuses deletion (F10). |
| Thumbnail fails | Log; no thumbnail; recording unaffected. |
| Disk full | `DiskGuard` stops the part; retry/cooldown; finalize may fail and be retried by recovery. |
| Part number reaches 1000 | Hard error; the session finalizes with the existing parts. |

## 11. Test matrix

Manifest and summary:

- Round-trip v1 manifest; unknown fields ignored; corrupt/empty manifest handled.
- Latest metadata for title/game; chapters skip the initial entry.
- `PartCount`/`SegmentCount`/`SizeBytes`/`DurationSeconds` derivation.
- `SizeBytes` excludes manifest, thumbnail, and part playlists.

Writes and locks:

- Atomic replace leaves the old file on a simulated write failure; `.tmp` cleanup.
- Concurrent open/append/finalize/stitch/delete: no deadlock, no torn manifest.
- `StitchLive` cache hit is lock-free; cache miss locks; finalize/reopen/delete invalidate.
- Delete removes the library entry before files; lock entry removed.

Stitching:

- Header, discontinuity, `#EXT-X-MAP`, part ENDLIST stripping, final ENDLIST, live no-ENDLIST.
- Half-written trailing EXTINF dropped.
- Target duration live drift; final stable.
- Chapters from the second metadata entry; DST/time-zone edge cases.
- Total duration matches the sum of segment durations.

Recovery:

- Newest unfinished per watched channel; older/paused/removed sessions finalized.
- Complete session becomes finished; missing index re-finalized.
- No manifest, corrupt manifest, unparseable part playlist: skipped and left untouched.
- `.tmp` deletion and orphan media cleanup only for valid sessions.
- `MarkLoaded` after recovery; library contains all valid sessions.

Library and disk:

- `Changed` on upsert/remove/loaded; `IsLoaded` transition.
- Zero-recording streamers and nullable `EndedAt`.
- Concurrent `Changed` subscribers.
- `DiskGuard` below/above `MinFreeGb`; no retention service.

End-to-end:

- Record a part, append metadata, finalize, serve the index, restart and recover.
- Crash during finalize, restart, recover.
- Delete a complete session while a media request is in flight.

## 12. Binding versus implementation freedom

Binding: manifest schema and status, derived summary fields, atomic-write rule, lock coverage, stitching rules, target-duration policy, chapter rule, recovery algorithm, library event/`IsLoaded` contract, disk guard, retention non-goal, and part-number bound.

Freedom: `SemaphoreSlim` dictionary details, JSON serializer options, cache-key implementation, file enumeration, logging, and test framework.

## 13. Related

- `docs/domain-model.md` for `Session`, `Resumable`, `RecordingLibrary` inputs.
- `docs/recorder-contract.md` for part files and finalize behaviour.
- `docs/reviews/implementation-plan-amendments.md` (A-02, A-03, A-08, A-09).
- `docs/adr/0004-retention-non-goal.md`.
