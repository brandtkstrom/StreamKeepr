# Stream Keepr Backend-for-UI Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, backend surface and F10; `docs/session-store-contract.md`; `docs/runtime-contract.md`; `docs/twitch-auth-contract.md`; plan review B-05/C-01/C-02/C-04/C-05/C-06.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract defines the in-process services and HTTP endpoints the Blazor UI consumes: library view models, channel views, progress, export, recording administration, storage monitoring, and authorization seams. Components never read files, run processes, or call Twitch; they call these services.

## 2. Library view models

```csharp
namespace StreamKeepr.Ui;

public sealed record StreamerView(
    string Login, string DisplayName, string? AvatarUrl,
    bool IsRecording, bool IsPaused, bool IsWatched,
    int RecordingCount, DateTimeOffset? LastRecordedAt);

public enum RecordingState { Recording, Reconnecting, Partial, Saved }

public sealed record RecordingView(
    string Id, string StreamerLogin, string Title, string? Category,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, double DurationSec,
    RecordingState State, int SegmentCount, int PartCount, long? SizeBytes,
    string? ThumbnailUrl, string PlaylistUrl, double? ResumeSec,
    IReadOnlyList<Chapter> Chapters);

public sealed record StorageView(long FreeBytes, long TotalBytes);

public sealed record LibraryQuery(string? State = null, string? Search = null, string Sort = "newest");

public interface ILibraryView : IDisposable
{
    event Action? Changed;
    IReadOnlyList<StreamerView> Streamers(LibraryQuery query);
    IReadOnlyList<RecordingView> Recordings(string streamerLogin, LibraryQuery query);
    RecordingView? Find(string id);
    StorageView Storage { get; }
}
```

- Scoped: one per circuit; subscribes to `RecordingLibrary.Changed`, `DvrStatus.Changed`, `StorageMonitor.Changed`, and `ProgressStore.Changed`; throttles `Changed` to at most every 500 ms; unsubscribes on dispose.
- `Streamers` includes streamers with at least one recording. A watched channel with zero recordings appears in `ChannelsView`, not the library. `LastRecordedAt` is nullable for safety.
- `RecordingView.DurationSec`: complete sessions use the manifest/index duration; recording/reconnecting sessions use elapsed time since `StartedAt` (non-negative).
- `SizeBytes` is null while recording; otherwise media bytes.
- `PlaylistUrl` is `/media/{channel}/{id}/index.m3u8`.
- `ResumeSec` is null for recording/reconnecting sessions and for positions within 30 s of the end.

### State derivation

| State | Rule |
|---|---|
| Recording | Manifest is `Recording` and `DvrStatus.ActiveJob.SessionId == RecordingView.Id` |
| Reconnecting | Manifest is `Recording` and no active job belongs to the session |
| Partial | Complete with more than one part |
| Saved | Complete with one part |

### Query, sort, and search

- `Sort`: `newest` (default), `oldest`, `az`. Invalid values fall back to `newest`.
- Streamers: recording streamers first, then `LastRecordedAt` desc for `newest`, asc for `oldest`, and `DisplayName`/`Login` ordinal for `az`. Tie-break `Login` ordinal.
- Recordings: `StartedAt` desc for `newest`, asc for `oldest`; `az` keeps newest-first inside a streamer. Tie-break `Id` ordinal.
- `State` filter: `recording`, `reconnecting`, `partial`, `saved`; case-insensitive; invalid means no filter. `recording` matches Recording and Reconnecting; `saved` matches Saved and Partial.
- Search: case-insensitive over title, category, streamer login, and display name; trimmed; empty means no filter.

## 3. Channel view models

```csharp
public enum ChannelRuntimeState { Recording, Reconnecting, Watching, Paused, CoolingDown }

public sealed record ChannelView(
    string Login, string DisplayName, string? AvatarUrl,
    bool IsWatched, bool IsPaused, ChannelRuntimeState State,
    bool PushConnected, string PushReason);

public interface IChannelsView : IDisposable
{
    event Action? Changed;
    IReadOnlyList<ChannelView> All { get; }
    Task AddAsync(string login, CancellationToken ct);
    Task RemoveAsync(string login, CancellationToken ct);
    Task StopAsync(string login, CancellationToken ct);
    Task ResumeAsync(string login, CancellationToken ct);
}
```

- Scoped and signed-in. `All` and every method throw `UnauthorizedAccessException` when the circuit has no authenticated admin. Components use `AuthorizeView`/`RequiresTwitch` for display; the service enforces.
- `All` lists watched channels.
- `State`: Recording when the active job is running; Reconnecting when the manifest is Recording without a job; Paused when the pause flag is set; CoolingDown when the reducer is cooling down; Watching otherwise.
- `PushConnected` is `PollSchedule.IsPushConnected` (all watched channels covered). `PushReason` is `EventSub connected`, `No admin token`, `Partial coverage`, `EventSub reconnecting`, or `Polling only`.
- `AddAsync` delegates to `IChannelControl.AddChannelAsync`, which validates the login through Helix and resolves the profile. Unknown/invalid logins throw `ArgumentException`/`InvalidOperationException` with a user-safe message.
- `RemoveAsync` finalizes the open session and keeps recordings. `StopAsync` persists `Paused` and routes `StopRequested`. `ResumeAsync` clears `Paused`, routes `ResumeRequested`, and nudges the poller.
- `Changed` fires on `ChannelStore.Changed`, `DvrStatus.Changed`, and push-status changes; throttle to 500 ms.

## 4. Progress store

```csharp
public sealed record ProgressRequest(double PositionSec);

public interface IProgressStore
{
    event Action? Changed;
    double? Get(string recordingId);
    Task SetAsync(string recordingId, double positionSec, CancellationToken ct);
    Task ClearAsync(string recordingId, CancellationToken ct);
}
```

- File: `/recordings/.streamkeepr/progress.json`, a JSON map of recording id to position.
- Load at startup; corrupt/missing file logs and starts empty.
- In-memory reads; atomic writes at most every 10 s and on graceful shutdown; no write per keystroke.
- `SetAsync` validates the recording exists, clamps to `[0, duration]`, and clears positions within 30 s of the end. Unknown id is a no-op for in-process callers; the HTTP endpoint returns 404.
- `ClearAsync` removes the entry; delete also clears it.
- `Changed` fires per update; `ILibraryView` throttles resume-bar updates.
- `PUT /api/recordings/{id}/progress` is public (viewing is open). Body `{ positionSec }`; 204 on success; 400 for a non-finite or negative position; 404 for an unknown recording. `Cache-Control: no-store`.

## 5. Export service

```csharp
public enum ExportState { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record ExportJob(
    string Id, string RecordingId, ExportState State, double Progress,
    string? FileName, string? Error);

public interface IExportService
{
    event Action? Changed;
    ExportJob Start(string recordingId, ClaimsPrincipal user, CancellationToken ct);
    ExportJob? Find(string exportId);
    ExportJob? FindByRecording(string recordingId);
    void Cancel(string exportId, ClaimsPrincipal user);
}
```

- **Always one MP4.** No ZIP fallback.
- `Start` requires an authenticated admin. The recording must be Complete; Recording/Reconnecting throws `InvalidOperationException`. Unknown id throws `KeyNotFoundException`.
- `FindByRecording` returns the most recent job for the recording: the active job while Queued/Running, otherwise the latest result. It returns null when no job exists or a Succeeded job's file has been cleaned up, so the details card can rehydrate after navigation or reload. `Find(exportId)` and the download endpoint keep their 404 semantics.
- `ffprobe` checks every part's video parameters before enqueueing. Incompatible parts produce a `Failed` job with `ExportIncompatibleParts`; no file is produced. The error value is the stable code the UI uses to suppress Retry; other error values are user-safe messages.
- One export at a time; FIFO queue. FFmpeg runs `-i <index> -c copy -movflags +faststart -progress pipe:1` into `.streamkeepr/exports/{exportId}.mp4`, writes a temp file, and renames atomically.
- `Progress` is 0..1 from `out_time_us` / recording duration; `Changed` fires at most every 500 ms.
- A lease (`IExportLeaseRegistry`) is held from start through success/failure/cancel. Delete refuses while the recording is leased.
- Cleanup removes export files older than 24 hours on startup and periodically; job entries whose file is gone return 404 on download.
- `GET /api/exports/{exportId}/download` requires sign-in. 200 returns the MP4 with `Content-Disposition: attachment` and range support; 404 unknown; 409 not succeeded; 401/403 unauthenticated.
- `Cancel` cancels a running FFmpeg or removes a queued job, releases the lease, deletes the partial file, and sets `Cancelled`.
- Expected failures throw `InvalidOperationException`/`KeyNotFoundException` with user-safe messages; incompatible parts are a failed job, not an exception.

## 6. Recording administration and storage

```csharp
public interface IRecordingAdmin
{
    Task DeleteAsync(string recordingId, ClaimsPrincipal user, CancellationToken ct);
}

public interface IStorageMonitor
{
    StorageView Current { get; }
    event Action? Changed;
}
```

- `DeleteAsync` requires an authenticated admin, only Complete sessions, takes `SessionStore.LockFor`, refuses while an export lease exists, removes the library entry, deletes the folder, clears progress, and releases the lease entry. Recording/Reconnecting throws `InvalidOperationException`; unknown id throws `KeyNotFoundException`.
- `StorageMonitor` samples `DiskGuard` every 60 s and after finalize/delete/export. `StorageView` is `(FreeBytes, TotalBytes)`; add `TotalBytes` to `DiskGuard` from `DriveInfo.TotalSize`. `Changed` fires only when free space moves by more than 1% of total.
- `ILibraryView.Storage` reads `StorageMonitor.Current`.

## 7. Authorization seams

| Service | Authorization |
|---|---|
| `ILibraryView` | Public read |
| `IProgressStore` | Public write (viewing is open) |
| `IChannelsView` | Signed-in for read and mutation |
| `IExportService.Start/Cancel` | Signed-in; `ClaimsPrincipal` parameter |
| `IExportService.Find/FindByRecording` | Public read of job metadata; the download itself stays signed-in |
| `IRecordingAdmin.DeleteAsync` | Signed-in; `ClaimsPrincipal` parameter |
| Export download endpoint | Signed-in |
| Media/health/static assets | Public |
| Settings page/components | Signed-in via `AuthorizeView`/`RequiresTwitch` |

Singletons never resolve scoped `AuthenticationState`; the scoped view or component passes the authenticated `ClaimsPrincipal` into the service. A disabled button is never the only guard.

## 8. F10 reshaping and dependency corrections

- **F10-T1 is absorbed into Part 1.** `PartCount`, `SegmentCount`, and `EndedAt` belong to `RecordingSummary`; profile/`UserId` resolution belongs to `ChannelStore` at add/seed. Delete or reduce F10-T1 to UI wiring.
- **F10-T7 is a prerequisite of F6-T1.** The admin token store and Data Protection keys precede sign-in; F6-T1 depends on F10-T7. F10-T7 does not depend on F6-T1.
- Remaining F10 tasks: T2 progress, T3 export, T4 recording admin, T5 storage monitor, T6 channels view.
- Corrected edges:
  - `F0-T3` → backend summary fields + `F10-T2` + `F10-T5` (no `F10-T1`).
  - `F1-T2` → `F0-T3`.
  - `F2-T3`, `F2-T4` → `F10-T2`.
  - `F9-T1` → `F10-T2`, `F10-T3`, `F6-T1`.
  - `F9-T2` → `F7-T1`/`T2`/`T3`, `F5-T4`.
  - `F10-T4` → `F10-T3` (lease registry).
  - `F9-T5` is split per the testing contract.
- Recompute the Part 2 waves from these edges; do not hand-edit only the wave table.

## 9. Test matrix

Library:

- State derivation for Recording/Reconnecting/Partial/Saved; active-job match.
- Null `LastRecordedAt`/`SizeBytes`; zero-recording channels only in Settings.
- Sort newest/oldest/az with deterministic ties; state filter; search; invalid query fallback.
- `Changed` throttling, unsubscribe on dispose, storage updates.

Channels:

- Unauthorized read/mutation throws; signed-in works.
- Add Helix validation success/unknown; remove/stop/resume; push reason variants.
- `Changed` on channel/status/push events.

Progress:

- Load/corrupt file; atomic write throttle; clamp; 30 s clear; delete clears; 204/400/404 endpoint.

Export:

- Queue one at a time; progress; incompatible parts fail; recording rejected; lease blocks delete; cancel; cleanup; download auth/range/404/409.
- `FindByRecording` rehydration for Queued/Running/Succeeded/Failed/Cancelled; latest job wins; null after file cleanup.

Admin/storage:

- Complete-only delete; lock; lease refusal; library/progress cleanup.
- Storage monitor 60 s/after-event sampling; >1% threshold; `TotalBytes`.

Graph/integration:

- No F6/F10 cycle; F0-T3/F1-T2/F2/F9 edges.
- bUnit library/channels components; WebApplicationFactory endpoints; Playwright export/delete/settings journeys.

## 10. Binding versus implementation freedom

Binding: view-model shapes and derivation, query/sort/state rules, `ILibraryView`/`IChannelsView` scoping and 500 ms throttle, progress file/endpoint semantics, export always-MP4/queue/lease/cleanup/download/`FindByRecording` contract, delete rules, storage threshold, authorization seams, and corrected F10 graph.

Freedom: service internals, FFmpeg process wiring, JSON serialization, timer implementation, error message wording, and test framework.

## 11. Related

- `docs/session-store-contract.md` for `RecordingSummary` and locks.
- `docs/runtime-contract.md` for `IChannelControl`/`DvrStatus`.
- `docs/twitch-auth-contract.md` for the admin token store.
- `docs/hosting-contract.md` for endpoints and auth.
- `docs/testing-contract.md` for test layers.
- `docs/reviews/implementation-plan-amendments.md` (B-05, C-01, C-02, C-04, C-05, C-06).
