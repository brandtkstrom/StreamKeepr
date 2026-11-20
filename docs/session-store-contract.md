# Stream Keepr Session Store Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 5; `docs/domain-model.md`; `docs/recorder-contract.md`; plan review A-02/A-03/A-08/A-09.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope and boundary

The session store owns the on-disk session manifest, part stitching, startup recovery, the in-memory recording library, and disk-space checks. The recorder owns part files; the store owns `session.json`, `index.m3u8`, `thumb.jpg`, and library summaries. The store never runs Streamlink/FFmpeg and never changes domain state. `ThumbnailService` grabs thumbnail frames with FFmpeg and hands each image to the store (§7). A session folder may also contain a `chat/` sidecar owned by `ChatLogStore`; the session store only derives `HasChat` and deletes the folder with the session. It may also contain `viewers.jsonl` (§7.2), which the store appends but never folds into `session.json`.

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

The `chat/` sidecar holds `meta.json` (advisory status, counts, lead-in, bucket length), `events.jsonl`, and message buckets named for their UTC start (`yyyyMMddTHHmmZ.jsonl`). A message row stores its Twitch fields plus `local: true` when `tmi-sent-ts` was missing and the capture clock stood in. Event kinds are `delete`, `clear_user`, `clear_all`, `roomstate` (including `uniqueChat`, Twitch's r9k mode), `notice` (the notice text), `gap`, `dropped`, `truncated`, `end`, and `purged`.

## 3. Atomic writes

- Manifest, stitched index, and thumbnail are written to `<name>.tmp` in the same directory, then renamed over the target with `File.Move(temp, target, overwrite: true)`.
- Atomicity relies on a local filesystem: the Docker POSIX volume in production, a local NTFS volume for Windows development. Network filesystems are not supported.
- Readers open with read/write/delete sharing, and a replace that loses a brief race with another handle on Windows is retried a bounded number of times before it fails. A concurrent lock-free read therefore never blocks or corrupts a write.
- A failed rename leaves the previous complete file and a `.tmp`; recovery deletes stale `.tmp` files.
- No fsync is required. A host power loss may lose the last metadata update; media files remain the source of truth.
- Thumbnails are best-effort, atomic, and written once. `ThumbnailService` writes the image under `.streamkeepr/thumbnails/` on the same volume; `SaveThumbnailAsync` renames it into place. An existing `thumb.jpg` is never replaced, because `/media` serves it as immutable. If a grab fails, log it; `HasThumbnail` stays false.

## 4. Locking

Two per-session synchronisation primitives exist:

- `SessionStore.LockFor(sessionDir)` — the manifest/lifecycle lock. It guards read-modify-write of `session.json`, lifecycle mutations, the append-only sidecars, and the final write of the index.
- The `LivePlaylistService` refresh gate — a per-session single-flight gate for background live-playlist builds. It guards no file; it only decides which refresh runs.

| Operation | Lock | Notes |
|---|---|---|
| `OpenAsync` | manifest | Takes the lock once, then calls core open/reopen. Never nests a locking `ReopenAsync`. |
| `ReopenAsync` | manifest | Public entry; internal `ReopenCoreAsync` assumes the lock. |
| `AppendMetadataAsync` | manifest | Read-modify-write is atomic under the lock. |
| `AppendViewerSampleAsync` | manifest | Background append to `viewers.jsonl`; never creates the folder. |
| `FinalizeAsync` | manifest | Writes index and manifest and updates the library; the live refresher prunes the snapshot on its next pass. |
| Recording-admin delete | manifest | Removes the library entry, deletes the files, and removes the lock entry; the endpoint returns 404 and the live refresher prunes the snapshot. |
| Recovery finalize | manifest | Calls `FinalizeAsync`. |
| `LivePlaylistService.RefreshOneAsync` | refresh gate | `WaitAsync(0)` single-flight: a busy session skips the tick. Never takes `LockFor`. |
| `ThumbnailService` live read | refresh gate, then manifest | `GetOrRefreshAsync` takes and releases the refresh gate; only then does `SaveThumbnailAsync` take the manifest lock. Never both. |
| `TryLoadAsync` | none | Read-only manifest projection; used at startup and by the refresher. |
| `SaveThumbnailAsync` | manifest | Checks the manifest still exists, renames the image to `thumb.jpg` without overwrite, updates the library. Never modifies the manifest. |

Rules:

- One-way lock ordering: never acquire `LockFor` while holding the refresh gate, and never acquire the refresh gate while holding `LockFor`. `SemaphoreSlim` is not reentrant; a nested acquire deadlocks.
- Hold at most one manifest lock at a time. Never acquire a second session's manifest lock while holding one.
- The live index endpoint takes neither lock and reads no session file. `RecordingLibrary` decides known/Recording/Complete; `LivePlaylistCache` supplies a Recording session's body.
- A live refresh reads the manifest and part playlists through `PartPlaylistReader`, which does not take `LockFor`. Requests and refreshes never wait on manifest/lifecycle I/O.
- Keep the lock entry for finalized sessions because they may reopen; remove it on delete.
- Recording-admin delete removes the library entry before deleting the directory, so new requests return 404 even while the lock is held; an in-flight request may also return 404/410, never a 500.

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

Live snapshot:

- `LivePlaylistCache` is a singleton keyed by session id. Each entry is an immutable `LivePlaylistSnapshot` (`Id`, `Version`, `Body`, pre-encoded UTF-8 `Utf8`, `SegmentCount`, `RefreshedAt`). Publishing swaps the reference atomically; a reader never observes a partial body.
- `LivePlaylistService` is a hosted `BackgroundService`. After the supervisor's recovery has loaded the library, it refreshes immediately and then every `LivePlaylist:RefreshSeconds` (default 1). Each session has a single-flight refresh gate: a busy session skips the tick rather than queueing.
- A refresh reads the manifest and part playlists through `PartPlaylistReader` (read/write/delete sharing, `FileOptions.Asynchronous`, `StreamReader` and the token) without the manifest lock. `SessionStore.StitchLiveAsync` no longer exists.
- Per session, the refresher keeps an immutable `PartSet(ImmutableArray<PartPlaylist> Parts, int LastPart, long LastWriteTicks, long LastLength)`. Parts below the active last part are closed and never re-read while a higher part exists. A changed last part and any new parts are re-read; if a part disappears or the file list is not a prefix of the previous set, the set is rebuilt from scratch.
- A refresh constructs the complete snapshot off to the side and publishes it atomically. A failed or timed-out read publishes nothing and keeps the last-known-good body.
- The refresher caches only `Recording` sessions. It prunes a snapshot when its summary leaves the library or is no longer `Recording`, and it re-checks the library before publishing: a refresh that races finalize never publishes a body for a Complete session.
- Finalize, reopen, and delete do not invalidate the cache directly. `RecordingLibrary` is the authority: finalize records Complete, the endpoint switches to the physical index immediately, and the refresher prunes on its next tick. Recording-admin delete removes the library entry first, so the endpoint returns 404 immediately.
- A refresh whose body is unchanged does not publish a new version or re-encode the body.
- Refreshes for different sessions run concurrently; a slow session does not delay the others.
- A refresh failure (`IOException`, `UnauthorizedAccessException`, corrupt/unreadable manifest, format error, or timeout) keeps the last-known-good body, logs with a per-session backoff, and never stops the host. One session's failure does not block another's refresh.
- The live index endpoint is a pure cache read. A Recording session with no snapshot returns 503 with `Retry-After: 1`. `Cache-Control: no-cache` is set on every index response, including 404 and 503.
- `ThumbnailService` calls `LivePlaylistService.GetOrRefreshAsync`: a live recording refreshes through the same gate and returns the current snapshot; a Complete recording reads the finalized index once and is not cached. If the session finalizes during a live refresh, the finalized body is returned instead of being discarded. The refresh gate is released before `SaveThumbnailAsync` takes the manifest lock.
- If the library summary is `Complete`, the endpoint serves the finalized `index.m3u8` file with HEAD and Range; never a live stitch without ENDLIST.

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

- `OpenAsync(session, live)`: under the lock, if `session.json` exists, reopen; otherwise create the directory, write manifest v1 with the first metadata entry, and upsert the library.
- `ReopenAsync(session)`: under the lock, set `Status = Recording`, clear `EndedAt`/`DurationSeconds`/`SizeBytes`, delete `index.m3u8`, write the manifest, and upsert the library; the live refresher republishes from the new Recording summary on its next tick. Reopen does not invalidate the cache directly.
- `AppendMetadataAsync(session, live)`: under the lock, read the manifest; if the latest title and game are unchanged, return. Otherwise append a metadata change, write the manifest, upsert the library with live counts and chapters.
- `FinalizeAsync(session)`: under the lock, collect non-empty part playlists in numeric order. If none, delete the session directory and remove the library entry. Otherwise stitch final, write `index.m3u8` atomically, set `Status = Complete`, `EndedAt`, `DurationSeconds`, `SizeBytes`, write the manifest, and project the library summary from the manifest, parts, index, and one `MediaSize` pass already in memory instead of re-reading them under the lock. Finalize does not invalidate the cache directly; the endpoint switches to the index on the next request, and the next live refresh sees a Complete summary and prunes the snapshot.
- Recording-admin delete: take the manifest lock, remove the library entry, delete the directory, and remove the lock entry; the endpoint returns 404 immediately and the next live refresh prunes the snapshot. Delete does not invalidate the cache directly. Export leases are checked by the caller. The session directory includes `chat/` and `viewers.jsonl`, so chat and viewer samples are deleted with the recording. `FinalizeAsync` does not rewrite chat; the chat writer closes and optionally purges deleted text on its own finalize path.
- `LivePlaylistService.GetOrRefreshAsync(summary)`: returns a live snapshot for a Recording session or the finalized index for a Complete session (thumbnail path only). The refresh gate is released before the caller takes the manifest lock.
- `SaveThumbnailAsync(dir, image)`: under the lock, if `session.json` is missing (deleted) or `thumb.jpg` exists, delete the image and return false; otherwise rename it to `thumb.jpg` and upsert the library.

### 7.1 Thumbnails (decision E-01: frame grab)

- `ThumbnailService` is a singleton `BackgroundService` registered after the supervisor. At start it clears `.streamkeepr/thumbnails/`, then sweeps the library once it is loaded and every minute after that. Sweeps never overlap; one FFmpeg runs at a time.
- Each recording without a thumbnail is a candidate. A finished recording uses the frame at `min(5 min, duration / 2)`. A recording still in progress waits until it has more than 5 minutes of media, then uses 5 minutes.
- The position is mapped onto the playlist obtained from `LivePlaylistService.GetOrRefreshAsync` with `PlaylistStitcher.SegmentAt`, which returns that segment's `#EXT-X-MAP` init file and media file as plain names in the session folder.
- FFmpeg decodes the first video frame of `concat:init_NNN.mp4|pNNN_XXXXX.m4s` (no HLS demuxing or seeking), scales it to at most 640 px wide without upscaling, and writes a JPEG. Recorded segments are independent, so the first frame is a keyframe. One grab is capped at 30 s.
- A grab failure is logged and retried on the next sweep, up to 3 attempts per recording per process lifetime.

### 7.2 Viewer samples (#186)

- `viewers.jsonl` in the session root is append-only, one camelCase object per line: `{"v":1,"at":123.4,"count":1658}`. `at` is seconds from `session.StartedAt`, rounded to 0.1 s and clamped to `>= 0`; `count` is the Helix `viewer_count`. `ViewerSamples` owns the format.
- `AppendViewerSampleAsync(session, at, count)` appends one line under the session lock. It never creates the session folder, so a sample after a delete fails instead of recreating it. If the file does not end in a newline (a torn write), it writes one first, so a partial line costs only itself.
- `ChannelCoordinator` samples after each `StreamStatusObserved` whose observation is `Live` with a non-null count, while the channel has an open session: `Recording`, or `WaitingToRetry` (reconnecting). The first such observation after the session opens samples at once; later ones wait until 60 s of observation time (`StreamStatusObserved.At`, not a timer) after the previous sample. `Unknown`, `Offline`, uncounted (EventSub-only) observations, and `CoolingDown` add nothing: a gap is a missing point, never a synthetic or flat sample. A failed write still starts the 60 s wait, so a persistent failure warns at most once a minute. The cadence is in memory, so a restart samples at the next observation.
- Sampling never blocks or fails recording: a write failure logs one warning and is otherwise ignored. No per-sample `Information` log.
- The file survives finalize, is not counted in `SizeBytes`, is never served by `/media` (`.jsonl` is not allowlisted), and is deleted with the session folder. There is no backfill; a session without the file has no samples.
- Reads (`ViewerSamples.ReadAsync`) are tolerant: a malformed, partial, wrong-version, or negative line is skipped with one warning per read, and at most 50,000 samples are returned.
- The Mock seeder writes deterministic samples every 10 s across each finished recording; the Mock poller gives each live observation a deterministic count, so the growing capture samples through the real coordinator path.

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
| Live request with an unloaded library | 503 + `Retry-After: 1`; no disk fallback. |
| Recording session with a cold live cache | 503 + `Retry-After: 1`; the player retries. |
| Live refresh fails | Last-known-good snapshot keeps serving; per-session backoff; the host stays up. A failed read publishes nothing. |
| Live read races a recorder write or replace | `PartPlaylistReader` shares read/write/delete. Windows may still reject an atomic replace until the read completes; the atomic writer retries a bounded number of times. |
| Refresh races finalize | The refresher re-checks the library before publishing and publishes nothing once the session is Complete; the endpoint serves the finalized index. The next refresh prunes the snapshot. |
| Delete races live request | Library entry removed first; the endpoint returns 404 immediately even if a snapshot briefly remains; the next refresh prunes it. |
| Delete races export | Export lease refuses deletion (F10). |
| Thumbnail fails | Log; no thumbnail; retried up to 3 times per process lifetime; recording unaffected. |
| Viewer sample write fails | One warning; the sample is lost; recording and status unaffected. |
| Torn `viewers.jsonl` line | Next append starts a new line; reads skip the partial line with a warning. |
| Delete races a thumbnail grab | The delete holds the session lock; `SaveThumbnailAsync` finds no manifest and discards the image. |
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
- Concurrent open/append/finalize/refresh/summarize/delete: no deadlock, no torn manifest.
- Holding the manifest lock does not block a live refresh or a live request.
- Holding the refresh gate does not block finalize, reopen, or delete; finalize and a blocked refresh do not deadlock.
- Concurrent finalize, refresh, and 50 live index requests produce no 500 and no deadlock; the first response after finalize returns has ENDLIST.
- Part reads share read/write/delete and use asynchronous options; a writer and a delete are not blocked by a live read.
- `LivePlaylistCache` publish/read replaces a whole immutable snapshot; concurrent readers see one valid version.
- The refresher publishes the stitched body, version, and segment count; a new part updates the body.
- A refresh re-reads only the changed last part and new parts; a closed part's changed file is ignored while a higher part exists.
- A refresh rebuilds from scratch when a part disappears or the file list is not a prefix.
- The refresher is single-flight per session; a busy session skips the tick.
- A failed or timed-out refresh, including an unreadable manifest, keeps the last-known-good body and backs off.
- A Complete session publishes nothing, and a session removed from the library prunes its live entry; reopen republishes.
- One failing session does not stop another from publishing.
- Delete removes the library entry before files; lock entry removed.
- Endpoint: cached body serves after the session directory is deleted; a deleted session is 404 immediately; cold cache and unloaded library return 503; HEAD matches GET's content length with no body; a client abort is not an error; Complete still supports HEAD/Range and exactly one ENDLIST.

Viewer samples:

- Line schema, offset rounding and clamp; survives finalize, stays out of `session.json`; append after delete fails without recreating the folder; torn last line terminated.
- Tolerant capped read; coordinator cadence (≤ 1 per 60 s of observation time), gaps for Unknown/Offline/uncounted, reconnecting sessions sample, write failure is one warning.

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

Freedom: `SemaphoreSlim` dictionary details, JSON serializer options, part-set implementation, file enumeration, logging, and test framework.

## 13. Related

- `docs/domain-model.md` for `Session`, `Resumable`, `RecordingLibrary` inputs.
- `docs/recorder-contract.md` for part files and finalize behaviour.
- `docs/reviews/implementation-plan-amendments.md` (A-02, A-03, A-08, A-09).
- `docs/adr/0004-retention-non-goal.md`.
