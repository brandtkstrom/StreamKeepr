# Stream Keepr Session Store Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 5; ADR-0014 (session records, #316); `docs/domain-model.md`; `docs/recorder-contract.md`; plan review A-02/A-03/A-08/A-09.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope and boundary

The session store owns the session records (`Sessions` and `SessionMetadataChanges` in the record store, ADR-0014), part stitching, startup recovery, the in-memory recording library, and disk-space checks. The recorder owns part files; the store owns the session records, `index.m3u8`, `thumb.jpg`, and library summaries. New sessions write no `session.json`; a legacy one is never read. The store never runs Streamlink/FFmpeg and never changes domain state. `ThumbnailService` grabs thumbnail frames with FFmpeg and hands each image to the store (§7). A session folder may also contain a `chat/` sidecar owned by `ChatLogStore`; the session store only derives `HasChat` and deletes the folder with the session. Viewer samples (§7.2) are rows in the record store's `ViewerSamples` table, keyed to the session but never folded into its record; a leftover `viewers.jsonl` is never read or written.

## 2. Session record

```csharp
using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

public enum SessionStatus { Recording, Complete }

public sealed record MetadataChange(DateTimeOffset At, string Title, string Game);

public sealed record Chapter(double StartSeconds, string Title, string Game);

public sealed record SessionRecord(
    string Id,
    string Channel,
    string StreamId,
    DateTimeOffset StartedAt,
    SessionStatus Status,
    ImmutableList<MetadataChange> Metadata,   // first = title at start; later = changes
    DateTimeOffset? EndedAt = null,
    double? DurationSeconds = null,
    long? SizeBytes = null,
    int? PartCount = null,
    int? SegmentCount = null,
    bool HasThumbnail = false,
    bool HasChatSession = false);
```

A record is one `Sessions` row plus its `SessionMetadataChanges` rows in time order (insertion order breaks ties). `Status` has only `Recording` and `Complete`; the UI's `Reconnecting`/`Partial` states are derived, not stored. `EndedAt`, `DurationSeconds`, `SizeBytes`, `PartCount`, and `SegmentCount` are written only by finalize and cleared by reopen: while a session records they are null in the database. `HasThumbnail` is set when `SaveThumbnailAsync` moves `thumb.jpg` into place. `HasChatSession` is true when a `ChatSessions` row exists.

### Derived summary fields

`RecordingSummary` adds `PartCount`, `SegmentCount`, and `EndedAt`. While Recording, derive from the part playlists and media and never store:

- `PartCount`: number of `pNNN.m3u8` files that have segments (`ParsedPart.HasSegments`: at least one complete segment line).
- `SegmentCount`: number of segment URI lines across the part playlists.
- `SizeBytes`: sum of `init_*.mp4`, `*.m4s`, and `index.m3u8`; excludes `thumb.jpg` and part playlists.
- `DurationSeconds` and `EndedAt`: null.

Once Complete, `PartCount`, `SegmentCount`, `DurationSeconds` (`PlaylistTimeline.Duration` of the session index), `SizeBytes`, and `EndedAt` come from the record; the library load reads no media for them. A Complete session whose `index.m3u8` is missing is not listed.

Always:

- `Title` and `Game`: latest `Metadata` entry.
- `HasThumbnail`: the record's flag.
- `Chapters`: metadata changes after the first, positioned against the live stitch (Recording) or the session index (Complete). A Complete session without title changes reads no file.
- `HasChat`: `HasChatSession`, or, until #317 moves chat into the record store, `chat/meta.json` or at least one `chat/*.jsonl` bucket exists. #317 removes the sidecar check.
- `IsRecording`: `Status == Recording`.

`SizeBytes` excludes `chat/`; the chat log has its own `Chat:MaxBytesPerSession` cap.

The `chat/` sidecar holds `meta.json` (advisory status, counts, lead-in, bucket length), `events.jsonl`, and message buckets named for their UTC start (`yyyyMMddTHHmmZ.jsonl`). A message row stores its Twitch fields plus `local: true` when `tmi-sent-ts` was missing and the capture clock stood in. Event kinds are `delete`, `clear_user`, `clear_all`, `roomstate` (including `uniqueChat`, Twitch's r9k mode), `notice` (the notice text), `gap`, `dropped`, `truncated`, `end`, and `purged`.

## 3. Atomic writes

- Session records are written in SQLite transactions (one per operation). The stitched index and thumbnail are written to `<name>.tmp` in the same directory, then renamed over the target with `File.Move(temp, target, overwrite: true)`.
- Atomicity relies on a local filesystem: the Docker POSIX volume in production, a local NTFS volume for Windows development. Network filesystems are not supported.
- Readers open with read/write/delete sharing, and a replace that loses a brief race with another handle on Windows is retried a bounded number of times before it fails. A concurrent lock-free read therefore never blocks or corrupts a write.
- Part playlists are written by FFmpeg, not the store: it writes `pNNN.m3u8.tmp` and renames it over `pNNN.m3u8`. Live readers enumerate only `pNNN.m3u8`, never `*.tmp`, and close each handle promptly, so a reader sees the old complete file or the new complete file and never a partial final file.
- A failed rename leaves the previous complete file and a `.tmp`; recovery deletes stale `.tmp` files.
- No fsync is required for the index or thumbnail. The record store uses `synchronous=FULL` (ADR-0014).
- Thumbnails are best-effort, atomic, written once, and recorded on the session (`HasThumbnail`). `ThumbnailService` writes the image under `.streamkeepr/thumbnails/` on the same volume; `SaveThumbnailAsync` renames it into place. An existing `thumb.jpg` is never replaced, because `/media` serves it as immutable. If a grab fails, log it; `HasThumbnail` stays false.

## 4. Locking

Two per-session synchronisation primitives exist:

- `SessionStore.LockFor(sessionDir)` — the record/lifecycle lock (still called the manifest lock below). It guards read-modify-write of the session record, lifecycle mutations, the append-only sidecars, and the final write of the index.
- The `LiveIndex` refresh gate — a per-session single-flight gate for background live-playlist builds. It guards no file; it only decides which refresh runs.

| Operation | Lock | Notes |
|---|---|---|
| `OpenAsync` | manifest | Takes the lock once, then calls core open/reopen. Never nests a locking `ReopenAsync`. |
| `ReopenAsync` | manifest | Public entry; internal `ReopenCoreAsync` assumes the lock. |
| `AppendMetadataAsync` | manifest | Read-modify-write is atomic under the lock. |
| `AppendViewerSampleAsync` | none | Background insert of one `ViewerSamples` row; never touches the folder. The row's foreign key fails a sample for a session without a record. |
| `FinalizeAsync` | manifest | Writes the index, then the Complete record, and updates the library; the live refresher prunes the snapshot on its next pass. |
| Recording-admin delete | manifest | Removes the library entry, deletes the files, and removes the lock entry; the endpoint returns 404 and the live refresher prunes the snapshot. |
| Recovery finalize | manifest | Calls `FinalizeAsync`. |
| `LiveIndex.RefreshOneAsync` | refresh gate | `WaitAsync(0)` single-flight: a busy session skips the tick. The gate also serializes the session's `LiveComposition` build. Never takes `LockFor`. |
| `ThumbnailService` live read | refresh gate, then manifest | `GetOrRefreshAsync` takes and releases the refresh gate; only then does `SaveThumbnailAsync` take the manifest lock. Never both. |
| `TryLoadAsync` | none | Read-only record read by session folder; used by the refresher and recording admin. A record-store failure throws. |
| `SaveThumbnailAsync` | manifest | Checks the record still exists, renames the image to `thumb.jpg` without overwrite, sets `HasThumbnail`, updates the library. |

Rules:

- One-way lock ordering: never acquire `LockFor` while holding the refresh gate, and never acquire the refresh gate while holding `LockFor`. `SemaphoreSlim` is not reentrant; a nested acquire deadlocks.
- Hold at most one manifest lock at a time. Never acquire a second session's manifest lock while holding one.
- The live index endpoint takes neither lock and reads no session file. `LiveIndex.Lookup(channel, id)` decides once what it serves (`NotFound`, `Unavailable`, `SessionIndex(path)`, or `Live(snapshot, age, stale)`), with `RecordingLibrary` as the authority for known/Recording/Complete; the endpoint only maps that to HTTP.
- The cached snapshot is the last-known-good (LKG) body. `LiveIndexSnapshot.PublishedAt` is when the current body was first published; the endpoint computes the age with `TimeProvider` and, at or above `LivePlaylist:StaleAfterSeconds`, still returns `200` with `X-StreamKeepr-Stale: true`. A cold cache is `503` + `Retry-After: 1` + `no-store`; the request never waits on the refresh gate, rebuilds, or reads the session folder. A client abort is `499` and is logged at `Debug`.
- Cache invalidation is library-driven: finalize, reopen, and delete do not mutate the cache from the lifecycle path. The endpoint switches to the finalized index or `404` immediately, and the refresher prunes the snapshot on its next tick; a session that is no longer Recording never serves its cached live body.
- A live refresh reads the session record and part playlists (through `PartPlaylistReader`) without taking `LockFor`. Requests and refreshes never wait on manifest/lifecycle I/O.
- The refresh gate also serializes the per-session `LiveComposition` build. The published snapshot is an immutable reference swap; a warm refresh only stats part metadata and reads no part content. When a session leaves the live set, its whole `LiveIndex` entry (gate, parsed parts, published snapshot, backoff) is removed. A build that raced the removal finishes on the orphaned entry, which nothing reads, so it cannot publish over the newer state.
- Keep the lock entry for finalized sessions because they may reopen; remove it on delete.
- Recording-admin delete removes the library entry before deleting the directory, so new requests return 404 even while the lock is held; an in-flight request may also return 404/410, never a 500.

## 5. Stitching

`PlaylistStitcher.Stitch(partPlaylists, minTargetDuration, final)`:

- Header: `#EXTM3U`, `#EXT-X-VERSION:7`, `#EXT-X-TARGETDURATION`, `#EXT-X-MEDIA-SEQUENCE:0`, `#EXT-X-PLAYLIST-TYPE:EVENT`, `#EXT-X-INDEPENDENT-SEGMENTS`.
- For each part in ascending numeric part order, strip the part header tags (`#EXTM3U`, version, target duration, media sequence, playlist type, independent segments, ENDLIST) and keep the body, including the part's `#EXT-X-MAP` and `#EXT-X-PROGRAM-DATE-TIME`.
- A part has segments only when `PlaylistStitcher.ParsePart` finds at least one complete segment line (`ParsedPart.HasSegments`); this is the one definition the store, finalize, recovery, and the live index use. Insert `#EXT-X-DISCONTINUITY` between parts that have segments; a part without segments adds no body and no discontinuity.
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

Timeline:

- `PlaylistTimeline` reads one playlist (part playlist, live index, or session index) once and answers `Duration`, `SegmentCount`, `FirstProgramDateTime`, `Chapters(metadata)`, and `SegmentAt(offset)` from that parse. `PlaylistStitcher` only parses part playlists and renders indexes.
- A segment is a URI line preceded by `#EXTINF`; a trailing `#EXTINF` without a URI is not a segment. A URI without `#EXTINF`, a negative or unreadable duration, or an unreadable `#EXT-X-PROGRAM-DATE-TIME` makes the playlist malformed: `Parse` throws `FormatException` and `TryParse` returns null. A program-date-time without an offset is read as UTC.
- The summary, finalize, recovery, chat timeline, and thumbnail paths use `Parse`, so a malformed playlist is a read failure there, as before.

Live snapshot:

- `LiveIndex` is a singleton with one entry per live session id: a single-flight refresh gate, the `LiveComposition` the last build produced, the published immutable `LiveIndexSnapshot` (`Id`, `Body`, pre-encoded UTF-8 `Utf8`, `PublishedAt`), and the refresh backoff. Publishing swaps the snapshot reference atomically; a reader never observes a partial body.
- `LiveIndexRefresher` is a hosted `BackgroundService`. After the supervisor's recovery has loaded the library, it calls `LiveIndex.RefreshAllAsync` immediately and then every `LivePlaylist:RefreshSeconds` (default 1). A busy session skips the tick rather than queueing.
- `IPartPlaylistSource` (one part's text) is the module's only internal seam; production reads through `PartPlaylistReader`, and tests count, fail, or hold reads with one shared double. Everything else is tested against real session folders.
- A refresh reads the session record, then the part playlists through `PartPlaylistReader` (read/write/delete sharing, `FileOptions.Asynchronous`, `StreamReader` and the token) without the manifest lock. `SessionStore.StitchLiveAsync` no longer exists.
- Per session, the entry keeps an immutable `LiveComposition(ImmutableArray<PartSnapshot> Parts, string Body)`. A `PartSnapshot` holds the `ParsedPart` plus the file length and last-write ticks. The refresh gate serializes builds.
- A refresh enumerates only `pNNN.m3u8` (never `*.tmp`) and stats every part. When every `(length, lastWriteUtc)` matches the snapshot, it returns the cached `Body` with zero part-content reads and without taking `LockFor`.
- Only the active last part may still grow. It is read through `PartPlaylistReader`; once a new part appears, the previous last part is read once more in case it received its final segments. Every other closed part is served from the snapshot and is never re-read, even if its file changes.
- Any change (a grown, truncated, or rewritten last part, a new, deleted, or inserted part, or a target-duration change) renders `Body` from the parsed parts in memory with `PlaylistStitcher.Render`, the same renderer the session index uses, never by re-reading closed parts. The live index therefore equals a full stitch of the same parts by construction. Rendering the whole body is cheap next to publishing it, which already encodes the whole body; #243's cost was disk I/O and the session lock, which these read rules avoid.
- A new last part with segments adds exactly one `#EXT-X-DISCONTINUITY` before its `#EXT-X-MAP`/body. A header-only new part adds nothing and contributes no discontinuity.
- `PlaylistStitcher.ParsePart` is the single part parser used by both `Stitch` and the live index. It requires a terminating line feed before accepting the last line, so a trailing `#EXTINF` or an unterminated URI is never exposed.
- A changed part that is unreadable or is not a complete playlist (`PlaylistStitcher.IsCompletePart`: `#EXTM3U` plus a terminating line feed) fails the build with `InvalidDataException`. FFmpeg renames whole part playlists into place, so this is a recorder-contract violation: `LiveIndex` treats it as a refresh failure (logged, backed off) and keeps its last-known-good body; no partial or corrupt body is published.
- A target-duration raise changes the header of the next rendered body.
- When a session leaves the live set (finalize or delete), its entry is removed, including its failure backoff. Reopen starts a new entry from a Recording summary and rebuilds from disk.
- A refresh constructs the new composition and snapshot off to the side and publishes the snapshot atomically. A failed or timed-out read publishes nothing and keeps the last-known-good body.
- The refresher caches only `Recording` sessions. It prunes a snapshot when its summary leaves the library or is no longer `Recording`, and it re-checks the library before publishing: a refresh that races finalize never publishes a body for a Complete session.
- Finalize, reopen, and delete do not touch the cache from the lifecycle path. `RecordingLibrary` is the authority: finalize records Complete, the endpoint switches to the physical index immediately, and the next refresh removes the session's live entry. Recording-admin delete removes the library entry first, so the endpoint returns 404 immediately.
- A refresh whose body is unchanged keeps the published snapshot instance: it does not re-encode the body or move `PublishedAt`. Publishing never re-parses the body.
- Refreshes for different sessions run concurrently; a slow session does not delay the others.
- A refresh failure (`IOException`, `UnauthorizedAccessException`, an unreadable session record (`DbException`), format error, or timeout) keeps the last-known-good body, logs one `Warning` on the first failure of a window and `Debug` for later failures in that window (reset on success), backs off per session, and never stops the host. One session's failure does not block another's refresh.
- The live index endpoint is a pure cache read through `LiveIndex.Lookup`. A Recording session with no snapshot returns 503 with `Retry-After: 1` and `Cache-Control: no-store`; a warm snapshot returns 200 with `no-cache`, and one at or above `LivePlaylist:StaleAfterSeconds` adds `X-StreamKeepr-Stale: true`. `Cache-Control: no-cache` is set on 200 and 404 index responses. The endpoint never waits for a refresh, and no live/LKG body contains `#EXT-X-ENDLIST`.
- `PublishedAt` moves only when a new body is published. An unchanged body keeps it; the stale marker therefore reports how long the current body has been served, not whether the last refresh succeeded.
- `ThumbnailService` calls `LiveIndex.GetOrRefreshAsync`: a live recording refreshes through the same gate and returns the current snapshot; a Complete recording reads the finalized index once and is not cached. If the session finalizes during a live refresh, the finalized body is returned instead of being discarded. The refresh gate is released before `SaveThumbnailAsync` takes the manifest lock.
- If the library summary is `Complete`, the endpoint serves the finalized `index.m3u8` file with HEAD and Range; never a live stitch without ENDLIST.

## 6. Recovery

Recovery runs once at startup, before any channel coordinator processes events. No recorder child may survive concurrently; the container teardown guarantees this.

Recovery iterates the session records (ADR-0014) and reconciles only their folders; it never scans the recordings root for sessions. A skipped session keeps its record and is never listed; recovery never deletes media it cannot account for. For each session record:

1. A folder under the recordings root without a record is ignored: never listed, cleaned, or deleted. A legacy `session.json` is never read.
2. Resolve the folder `recordingsRoot/{channel}/{id}`. If the id or channel is unsafe, or the folder is missing: log and skip; keep the record.
3. If `Status == Complete`:
   - If `index.m3u8` is a final index (`PlaylistStitcher.IsFinalIndex`: it parses, has at least one segment, and has an `#EXT-X-ENDLIST` line), add `Resumable(Session, LastPart + 1, Complete: true)` to finished.
   - If the index is missing or not final (the reopen crash window, or a lost index) but a part playlist has segments (`ParsedPart.HasSegments`), re-finalize: re-stitch from the parts, write the index atomically, store the recomputed aggregates on the Complete record, and upsert the library; then add to finished.
   - If no part has segments, log and skip; do not serve, and leave media untouched (finalize would delete the folder as empty).
4. If `Status == Recording`:
   - Delete stale `*.tmp` files.
   - Ignore `chat/`; chat recovery is independent and append-only.
   - Parse every part playlist. If any parse fails, log and skip the session; leave media untouched. An unreadable `index.m3u8` also skips the session.
   - Delete `*.m4s` and `init_*.mp4` referenced by neither a part playlist nor an existing `index.m3u8` (`PlaylistStitcher.ReferencedMedia`). Init files come from `PlaylistTimeline.TryReadMapUri`, the same `#EXT-X-MAP` rule thumbnails use, so a map with trailing attributes such as `BYTERANGE` still names its file. The set is deliberately looser than `ParsePart`: an unterminated last line still counts, so media whose playlist line was still being written is never deleted.
   - Part playlists and the index are read through `PartPlaylistReader`.
   - If `index.m3u8` is a final index, this is the finalize crash window (index written, record not yet Complete) or a reopen that died between its record write and the index delete; no recorder ran after the index. If a part has segments, re-finalize as in step 3 (the record becomes Complete with recomputed aggregates) and add to finished; otherwise log and skip, keeping the media.
   - Otherwise add `Resumable(Session, LastPart + 1, Complete: false)` to unfinished.
5. Per watched channel:
   - For watched, unpaused channels, keep the newest unfinished session as `Unfinished`, even one without media yet; its next part still comes from the part files.
   - Finalize every other unfinished session (older, paused, or removed) and add it to finished. A session without media is deleted by that finalize: the record first (its metadata, chat, viewer, and resume rows cascade), then its folder. A session whose parts have no complete segment line but whose folder still holds `*.m4s` media (a hard kill mid-line) is logged and skipped instead, so its media is never deleted.
   - Keep the newest finished session as `Finished`.
6. Return `RecoveredChannel(Unfinished, Finished)` per watched channel.
7. Load the library from the session records (`RecordingLibrary.LoadAsync`), which marks it loaded, then remove every skipped session from it. The load itself never lists a record whose folder is missing, so a Recording record without a folder cannot appear with zero parts.

The supervisor maps `Unfinished` to `Idle.Resume` and `Finished` to `Idle.Finished`; a paused channel starts as `Paused(Finished)` rather than resuming. `LastPart` parses the numeric part from `pNNN.m3u8`.

## 7. Session operations

- `OpenAsync(session, live)`: under the lock, if the session has a record, reopen; otherwise create the directory, then insert the Recording row and its first metadata change in one transaction, and upsert the library. A failed insert throws: the recorder does not start, and the reducer retries (ADR-0014 write-failure policy). The empty folder it may leave has no record and is ignored.
- `ReopenAsync(session)`: under the lock, set the record to `Recording` and clear `EndedAt`/`DurationSeconds`/`SizeBytes`/`PartCount`/`SegmentCount`, then delete `index.m3u8` and upsert the library; the live refresher republishes from the new Recording summary on its next tick. The record changes first, so a failed write leaves the session Complete with its index. The prior finalize/delete prune has already released the IO snapshot, so the republish rebuilds from the parts.
- `AppendMetadataAsync(session, live)`: under the lock, read the record; if the latest title and game are unchanged, return. Otherwise insert a metadata change and upsert the library with live counts and chapters. A failed insert throws and changes nothing; the coordinator logs it and keeps recording.
- `FinalizeAsync(session)`: under the lock, collect the part playlists that have segments in numeric order. If none, delete the record, remove the library entry, and delete the session directory. Otherwise stitch final, write `index.m3u8` atomically, then mark the record `Complete` with `EndedAt` (kept if already set), `DurationSeconds`, `SizeBytes`, `PartCount`, and `SegmentCount`, and project the library summary from the record, parts, and index already in memory instead of re-reading them under the lock. The record is the commit point: if its write fails, the session stays Recording in the record and the library, and startup recovery finalizes it. Finalize does not invalidate the cache directly; the endpoint switches to the index on the next request, and the next live refresh sees a Complete summary and removes the session's live entry.
- `SeedAsync(session, metadata, endedAt)`: Mock seeding only. Records a Recording session with a known metadata history without touching its folder; a following finalize keeps `endedAt`.
- Recording-admin delete: take the manifest lock, remove the library entry, delete the session record (its metadata rows cascade), delete the directory, and remove the lock entry; the endpoint returns 404 immediately and the next live refresh removes the session's live entry. Export leases are checked by the caller. The session directory includes `chat/`, so chat is deleted with the recording; the session record's viewer-sample and resume-position rows cascade with it. `FinalizeAsync` does not rewrite chat; the chat writer closes and optionally purges deleted text on its own finalize path.
- `LiveIndex.GetOrRefreshAsync(summary)`: returns a live snapshot for a Recording session or the finalized index for a Complete session (thumbnail path only). The refresh gate is released before the caller takes the manifest lock.
- `SaveThumbnailAsync(dir, image)`: under the lock, if the session has no record (deleted) or `thumb.jpg` exists, delete the image and return false (recording an existing unrecorded `thumb.jpg`); otherwise rename it to `thumb.jpg`, set `HasThumbnail`, and upsert the library.

### 7.1 Thumbnails (decision E-01: frame grab)

- `ThumbnailService` is a singleton `BackgroundService` registered after the supervisor. At start it clears `.streamkeepr/thumbnails/`, then sweeps the library once it is loaded and every minute after that. Sweeps never overlap; one FFmpeg runs at a time.
- Each recording without a thumbnail is a candidate. A finished recording uses the frame at `min(5 min, duration / 2)`. A recording still in progress waits until it has more than 5 minutes of media, then uses 5 minutes.
- The position is mapped onto the playlist obtained from `LiveIndex.GetOrRefreshAsync` with `PlaylistTimeline.SegmentAt`, which returns that segment's `#EXT-X-MAP` init file and media file as plain names in the session folder.
- FFmpeg decodes the first video frame of `concat:init_NNN.mp4|pNNN_XXXXX.m4s` (no HLS demuxing or seeking), scales it to at most 640 px wide without upscaling, and writes a JPEG. Recorded segments are independent, so the first frame is a keyframe. One grab is capped at 30 s.
- A grab failure is logged and retried on the next sweep, up to 3 attempts per recording per process lifetime.

### 7.2 Viewer samples (#186)

- Samples are rows in the record store's `ViewerSamples` table (ADR-0014, #318): session id, `OffsetSeconds`, `Count`. The offset is seconds from `session.StartedAt`, rounded to 0.1 s and clamped to `>= 0`; `count` is the Helix `viewer_count`. `ViewerSamples` owns the table. `viewers.jsonl` is neither read nor written; a leftover one is ignored.
- `AppendViewerSampleAsync(session, at, count)` inserts one row per observation. It takes no session lock and never touches the session folder; the row's foreign key makes a sample after a delete fail instead of resurrecting the session.
- `ChannelCoordinator` samples after each `StreamStatusObserved` whose observation is `Live` with a non-null count, while the channel has an open session: `Recording`, or `WaitingToRetry` (reconnecting). The first such observation after the session opens samples at once; later ones wait until 60 s of observation time (`StreamStatusObserved.At`, not a timer) after the previous sample. `Unknown`, `Offline`, uncounted (EventSub-only) observations, and `CoolingDown` add nothing: a gap is a missing point, never a synthetic or flat sample. A failed write still starts the 60 s wait, so a persistent failure warns at most once a minute. The cadence is in memory, so a restart samples at the next observation.
- Sampling never blocks or fails recording: a write failure logs one warning and is otherwise ignored. No per-sample `Information` log.
- Samples survive finalize and restarts, are not counted in `SizeBytes`, and cascade when the session record is deleted. There is no backfill; a session without rows has no samples.
- Reads (`ViewerSamples.ReadAsync`) return the session's samples oldest first (by offset, then insertion), at most 50,000; a session without samples, or without a record, returns none.
- `MockViewerSeeder` inserts deterministic samples every 10 s across each finished recording (the Mock record store starts empty each process, so it always seeds); the Mock poller gives each live observation a deterministic count, so the growing capture samples through the real coordinator path.

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

- `RecordingLibrary` is an in-memory index loaded from the session records; nothing scans the disk per page load, and folders without a record are never listed.
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
| Session record write fails on open | The recorder does not start; the part ends "open failed" and the reducer retries or cools down. |
| Metadata change write fails | Logged; recording continues; the change is lost. |
| Crash or record write failure during finalize | Index may be written; the record still says Recording; recovery re-finalizes it to Complete with recomputed aggregates, even as the newest session of a watched channel. |
| Crash or record write failure during reopen | The record still says Complete with its index; the session reopens on the next live observation. A crash after the record write leaves a Recording record with a final index, which recovery re-finalizes to Complete; a Complete record that lost its index is re-stitched from the parts. |
| Session folder without a record | Ignored; never listed or deleted. A legacy `session.json` is never read. |
| Record whose folder is missing | Log and skip; keep the record; not listed, even by a library load. |
| Recording record without media | The newest of a watched channel resumes; any other is deleted with its folder (dependent rows cascade). If its folder still holds segment media, log and skip; never delete the media. |
| Unparseable part playlist | Log and skip the session; never delete media. |
| Complete session with missing index | Re-finalize from parts; if no parts, log and skip. |
| Missing segment referenced by a playlist | Log; do not rewrite the playlist; playback may 404. |
| Live request with an unloaded library | 503 + `Retry-After: 1` + `no-store`; no disk fallback. |
| Recording session with a cold live cache | 503 + `Retry-After: 1` + `no-store`; the player retries. |
| Live snapshot older than `StaleAfterSeconds` | 200 last-known-good body + `X-StreamKeepr-Stale: true` + `no-cache`; still no ENDLIST. |
| Live request aborted by the client | 499/no response; `Debug` request event; never 500/`Error`. |
| Live refresh fails | Last-known-good snapshot keeps serving; one `Warning` per failure window then `Debug`; per-session backoff; the host stays up. A failed read publishes nothing. |
| Live read races a recorder write or replace | `PartPlaylistReader` shares read/write/delete. Windows may still reject an atomic replace until the read completes; the atomic writer retries a bounded number of times. |
| Refresh races finalize | The refresher re-checks the library before publishing and publishes nothing once the session is Complete; the endpoint serves the finalized index. The next refresh prunes the snapshot. |
| Delete races live request | Library entry removed first; the endpoint returns 404 immediately even if a snapshot briefly remains; the next refresh prunes it. |
| Delete races export | Export lease refuses deletion (F10). |
| Thumbnail fails | Log; no thumbnail; retried up to 3 times per process lifetime; recording unaffected. |
| Viewer sample write fails | One warning; the sample is lost; recording and status unaffected. |
| Delete races a thumbnail grab | The delete holds the session lock; `SaveThumbnailAsync` finds no record and discards the image. |
| Disk full | `DiskGuard` stops the part; retry/cooldown; finalize may fail and be retried by recovery. |
| Part number reaches 1000 | Hard error; the session finalizes with the existing parts. |

## 11. Test matrix

Manifest and summary:

- Open/metadata/reopen/finalize write the session record; a folder without a record has none; no `session.json` is written.
- Write-failure policy: a failed open records nothing; a failed metadata write changes nothing; a failed finalize leaves the session Recording; a failed reopen leaves it Complete with its index.
- Latest metadata for title/game; chapters skip the initial entry.
- `PartCount`/`SegmentCount`/`SizeBytes`/`DurationSeconds` derivation.
- `SizeBytes` excludes the thumbnail and part playlists; Complete aggregates and the thumbnail flag come from the record after a restart.

Writes and locks:

- Atomic replace leaves the old file on a simulated write failure; `.tmp` cleanup.
- Concurrent open/append/finalize/refresh/summarize/delete: no deadlock, no torn record.
- Holding the manifest lock does not block a live refresh or a live request.
- Holding the refresh gate does not block finalize, reopen, or delete; finalize and a blocked refresh do not deadlock.
- Concurrent finalize, refresh, and 50 live index requests produce no 500 and no deadlock; the first response after finalize returns has ENDLIST.
- Part reads share read/write/delete and use asynchronous options; a writer and a delete are not blocked by a live read.
- `LiveIndex` publish/find replaces a whole immutable snapshot; concurrent readers see one valid version.
- A session that finalizes, leaves the library, or loses its folder has no live entry left; a reopened session starts without the old failure backoff.
- The refresher publishes the stitched body and its UTF-8 bytes; a new part updates the body.
- `Lookup` is unavailable before the library loads or before a Recording session's first publish, not found for an unknown session or another channel, the session index path once the library records Complete (even while the live entry remains), and live with `Stale` at or above `StaleAfterSeconds`.
- A warm refresh performs zero part-content reads and returns the cached body; `.tmp` is never read.
- A grown last part is the only part read, and the live index equals a full stitch.
- A new part adds one discontinuity before its map; a header-only new part adds nothing.
- Truncate, deletion, insertion, and target-duration change render from the parts in memory and equal a full stitch; an earlier part that changes is never re-read.
- Finalize deletes a session whose only segment line is unterminated; recovery never deletes media named by an unterminated line.
- An unterminated tail, a changed part that is not a complete playlist, and a failed read are logged refresh failures that keep the last good body and never publish a partial or corrupt playlist.
- A concurrent temp-file writer and many readers see only complete, parseable playlists whose segment count never decreases; the final body equals a full stitch.
- The refresher is single-flight per session; a busy session skips the tick.
- A failed or timed-out refresh, including an unreadable session record, keeps the last-known-good body and backs off; one `Warning` per failure window, `Debug` after that, reset on success.
- A Complete session publishes nothing, and a session removed from the library prunes its live entry and releases the IO parsed parts; reopen republishes from disk.
- One failing session does not stop another from publishing.
- Delete removes the library entry before files; lock entry removed.
- Endpoint: cached body serves after the session directory is deleted; a deleted session is 404 immediately; cold cache and unloaded library return 503 + `Retry-After: 1` + `no-store`; a snapshot at the staleness threshold returns 200 with `X-StreamKeepr-Stale: true` and a fresh one omits it; HEAD matches GET's content length for fresh and LKG bodies with no body; a client abort is 499/empty, not an error; Complete still supports HEAD/Range and exactly one ENDLIST.

Viewer samples:

- Row per observation, offset rounding and clamp; survives finalize and restart, stays out of the session record; append without a record fails without creating the folder; delete cascades the rows; no `viewers.jsonl`.
- Oldest-first capped read, none for a session without samples; coordinator cadence (≤ 1 per 60 s of observation time), gaps for Unknown/Offline/uncounted, reconnecting sessions sample, write failure is one warning.

Stitching:

- Header, discontinuity, `#EXT-X-MAP`, part ENDLIST stripping, final ENDLIST, live no-ENDLIST.
- Half-written trailing EXTINF and unterminated trailing URIs dropped; a part parser and full stitch share one implementation.
- Target duration live drift; final stable.
- Chapters from the second metadata entry; DST/time-zone edge cases.
- Total duration matches the sum of segment durations.

Recovery:

- Newest unfinished per watched channel; older/paused/removed sessions finalized.
- Complete session becomes finished; missing index re-finalized with updated aggregates.
- Finalize crash window: a Recording record with a final index becomes Complete with recomputed aggregates; so does a reopen that died before deleting the index.
- A Recording record with a final index but no part playlists keeps its media and is skipped.
- An older Recording record without media is deleted with its dependent rows and folder; one whose only segment line is unterminated keeps its media.
- Folders without a record ignored; a Complete or Recording record with a missing folder, or with an unparseable part playlist, skipped, kept, not listed, and left untouched.
- A restart loads the library from the record store; a finalize or reopen whose record write failed recovers as Complete.
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

Binding: session record schema and status, derived summary fields, atomic-write rule, lock coverage, stitching rules, target-duration policy, chapter rule, recovery algorithm, library event/`IsLoaded` contract, disk guard, retention non-goal, and part-number bound.

Freedom: `SemaphoreSlim` dictionary details, JSON serializer options, part-set implementation, file enumeration, logging, and test framework.

## 13. Related

- `docs/domain-model.md` for `Session`, `Resumable`, `RecordingLibrary` inputs.
- `docs/recorder-contract.md` for part files and finalize behaviour.
- `docs/reviews/implementation-plan-amendments.md` (A-02, A-03, A-08, A-09).
- `docs/adr/0004-retention-non-goal.md`.
