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
    IReadOnlyList<Chapter> Chapters, bool HasChat = false);

public sealed record StorageView(long FreeBytes, long TotalBytes);

public sealed record LibraryQuery(string? State = null, string? Search = null, string Sort = "newest");

public interface ILibraryView : IDisposable
{
    event Action? Changed;
    bool IsLoaded { get; }
    IReadOnlyList<StreamerView> Streamers(LibraryQuery query);
    IReadOnlyList<RecordingView> Recordings(string streamerLogin, LibraryQuery query);
    IReadOnlyList<RecordingView> AllRecordings(LibraryQuery query);
    RecordingView? Find(string id);
    StorageView Storage { get; }
}
```

- Scoped: one per circuit; subscribes to `RecordingLibrary.Changed`, `ChannelStore.Changed`, `DvrStatus.Changed`, `StorageMonitor.Changed`, and `ProgressStore.Changed`; throttles `Changed` to at most every 500 ms; unsubscribes on dispose.
- `AllRecordings` merges every streamer's recordings that match the query, in the query's recording order (ties by `Id`); the Home rows read the Recording and Saved sets from it (#182, `docs/library-contract.md` §10). This is additive.
- `ILibraryView` also exposes `bool IsLoaded` so the drawer can gate its skeleton rows without reading `RecordingLibrary` directly. This is additive to the view-model contract.
- `Streamers` includes streamers with at least one recording. A watched channel with zero recordings appears in `ChannelsView`, not the library. `LastRecordedAt` is nullable for safety.
- `RecordingView.DurationSec`: complete sessions use the manifest/index duration; recording/reconnecting sessions use elapsed time since `StartedAt` (non-negative).
- `SizeBytes` is null while recording; otherwise media bytes.
- `PlaylistUrl` is `/media/{channel}/{id}/index.m3u8`.
- `ResumeSec` is the recording's resume position, or null when it has none. Recording/reconnecting sessions have one once a viewer leaves the live edge; complete sessions never hold one within 30 s of the end.
- `HasChat` is true when the session's `chat/` folder has metadata or at least one bucket; it gates the chat pane.

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
- `PushConnected` is per channel: true when `PushStatus.Covers(login)` (this channel has both `stream.online` and `stream.offline`). It does not mean the poller uses the 300 s interval; that still needs every channel covered.
- `PushReason`: `EventSub connected` for a covered channel. With a live session (`Connected`/`Partial coverage`), an uncovered channel reads `User ID not resolved yet` when it has no `UserId`, otherwise `Not subscribed: refused or over capacity`. Without a session every channel shares the session reason: `No admin token` (WebSocket transport only, ADR-0007), `EventSub reconnecting`, or `Polling only`.
- `AddAsync` delegates to `IChannelControl.AddChannelAsync`, which validates the login through Helix and resolves the profile. Unknown/invalid logins throw `ArgumentException`/`InvalidOperationException` with a user-safe message.
- `RemoveAsync` finalizes the open session and keeps recordings. `StopAsync` persists `Paused` and routes `StopRequested`. `ResumeAsync` clears `Paused`, routes `ResumeRequested`, and nudges the poller.
- `Changed` fires on `ChannelStore.Changed`, `DvrStatus.Changed`, and push-status changes; throttle to 500 ms.

## 3a. Connection status view

```csharp
public enum EventSubConnection { Connected, Reconnecting, WaitingForAdmin, Off }

public sealed record ConnectionStatus(
    string Transport, EventSubConnection Connection, int Covered, int Watched,
    HealthStatus Health, string? HealthDescription, DateTimeOffset Now,
    DateTimeOffset? LastMessageAt, DateTimeOffset LastPollAt, DateTimeOffset? LastSuccessfulPollAt,
    TimeSpan? ApiRoundTrip, TimeSpan? PollInterval, TimeSpan UncoveredPollInterval);

public interface IConnectionStatusView : IDisposable
{
    event Action? Changed;
    Task<ConnectionStatus> GetAsync(CancellationToken ct);
}
```

- Scoped and signed-in; `GetAsync` throws `UnauthorizedAccessException` without an authenticated admin.
- `Transport` is `IEventSubClient.Transport`. `Connection`: `Connected` for push state Connected or Partial coverage, `Reconnecting`, `WaitingForAdmin` for No admin token, `Off` for Polling only.
- `Covered`/`Watched` come from `PushStatus`. Health is the `dvr` health check (`DvrHealthCheck.Name`) through `HealthCheckService`; a throwing check reports Unhealthy, “Health check failed.”
- Liveness comes from `PollSchedule`: `LastEventSubMessageAt`, `LastPollAt` (any attempt), `LastSuccessfulPollAt` (the same instant as `LastPollAt` when that poll succeeded) and `LastApiRoundTrip` (Helix time of the last successful poll that made requests), and `CurrentInterval` (the last wait's choice).
- `Changed` fires on push-status changes (500 ms throttle) and immediately on sign-in changes. Liveness values never raise it; the panel re-reads every 5 s.

## 3b. Chat replay

- `GET /api/recordings/{id}/chat?fromSec=&toSec=&limit=&syncOffsetSec=` is public and read-only. It returns a bounded media-time window from the session's chat log; `Cache-Control: no-store`.
- `fromSec` is required and non-negative; `toSec` defaults to `fromSec + 120` and the window is capped at 600 s. `limit` defaults to 300 and is capped at 1000. `syncOffsetSec` is clamped to ±30 s.
- The response envelope is `v`, `recordingId`, `state` (`recording` | `complete` | `starting` | `unavailable`), `hasChat`, `source` (`irc-anonymous` | `irc-token` | `mock` | `none`), `leadInSec`, `fromSec`, `toSec`, `syncOffsetSec`, `messages`, `gaps`, `hasMoreBefore`, `hasMoreAfter`. Messages carry `id`, `mt`, `at`, user fields, badges, text, emote ranges, bits, reply, and the action flag.
- Each gap is `{from, to, seconds}`: `from`/`to` in media seconds clipped to the window, `seconds` the wall-clock length for the divider. Gaps come from IRC outages (last message to rejoin), queue overflow (`dropped`), a truncated log (from the cap to the window end), and unrecorded time between parts, which is a single media point (`from == to`) because the stitched timeline is contiguous.
- Deleted and cleared messages are hidden, and a surviving reply's `reply` is `null` when it quotes removed text, whether or not purge ran.
- Unknown recordings are 404; malformed or out-of-range queries are 400; a recording without chat is 200 with `hasChat:false`. Raw `.jsonl` files are never served.
- The pane receives the endpoint's `limit` and sync range in its load payload. It keeps one fetch in flight; after a failure it waits 1 s, doubling to 30 s, before trying again. Seeks and sync changes replace the request in flight.

## 3c. Emote catalogs

- `GET /api/recordings/{id}/emotes` is public and read-only. It returns the third-party emote catalog for the recording's channel; `Cache-Control: no-store`.
- The response envelope is `v`, `available`, `stale`, `fetchedAt`, `entries`. Each entry is `{provider, id, code, animated, src, srcset}`, where `provider` is `bttv` or `7tv`. Providers are ordered by registration, so BetterTTV precedes 7TV. Entries are ordered by that precedence and deduped by exact, case-sensitive `code`; the pane takes the first entry for a code. Entries never include modifier/zero-width emotes. Images are the static first frame unless `Chat:Emotes:Animated` is on, in which case `src`/`srcset` point at the animated asset. `animated` is the emote's intrinsic flag, not a statement about which asset the URL names, so it stays true when a static frame is served. Image URLs are provider-specific: BetterTTV serves `/emote/{id}/{1x,2x,3x}.webp` and `/emote/{id}/static/{size}.webp`, 7TV serves `/emote/{id}/{size}.webp` and `/emote/{id}/{size}_static.webp` for the still frame of an animated emote.
- `available` is true when at least one enabled provider returned a catalog (including an empty one) or served a stale cache. `stale` is true when any served catalog is stale. `fetchedAt` is the earliest successful catalog fetch time among the catalogs used to build the response, or `null` when `available` is false.
- Unknown recordings are 404. A disabled feature or a provider failure with no catalog in either scope is 200 with `available:false` and an empty `entries` array; provider errors never block chat.
- The endpoint resolves the channel's numeric Twitch user ID at replay (`ChannelStore`, then Helix). If unresolved, it returns global entries only. No session field is stored.
- The endpoint caches global catalogs per provider and channel catalogs per provider and channel ID for five minutes. Each cache key has its own single-flight refresh, stale fallback, and failure boundary, so one scope's failure never discards the other's catalog; provider work is bounded to three seconds.
- The pane's load payload carries `emotesEnabled`, derived from the registered `IEmoteProvider` instances (true when any reports enabled). The pane fetches the catalog once per recording load only when open and enabled; a failed fetch leaves text rendering.
- Third-party entries are optional decoration. Chat text and Twitch emotes never depend on them.

## 3d. Badge catalogs

- `GET /api/recordings/{id}/badges` is public and read-only. It returns Twitch's global chat badge catalog for the recording; `Cache-Control: no-store`. The recording is validated so an unknown id is 404, but badge data is global in v1; the channel scope (#277) extends the same envelope.
- The response envelope is `v`, `available`, `stale`, `fetchedAt`, `global`, `channel`. Each entry is `{set, version, title, src, srcset}`. `channel` is present and always empty in v1.
- `global` is ordered as Twitch publishes it. `src` is `image_url_1x` and `srcset` is the supplied 1x/2x/4x URLs with those descriptors (Twitch's 4x URL ends in `/3`). URLs are used verbatim after validation.
- `available` is true when a catalog was fetched, including an empty one, or served from the last good cache. `stale` is true when the served catalog is older than the 60-minute TTL. `fetchedAt` is that catalog's fetch time, or `null` when `available` is false.
- A fetch failure never returns a non-2xx status. It returns 200 with the stale catalog and `stale:true`, or, with no cache, `available:false` and empty lists. Badge failures never affect chat text, emotes, capture, recording, or `/health/*`.
- The server fetches `GET https://api.twitch.tv/helix/chat/badges/global` through the Helix client with an app access token from `TwitchTokenProvider` and the configured `Client-Id`. A 401 invalidates that token once and retries; a token failure is an ordinary failed fetch. Neither the token nor the response body is logged or sent to the browser.
- The body is capped at 4 MiB, the catalog at 500 sets and 100 versions per set. Set and version IDs are 1–64 characters of `[a-z0-9_-]`; titles are capped at 128 characters. Every version must publish valid absolute `https:` 1x/2x/4x URLs on `static-cdn.jtvnw.net`; otherwise it is dropped. `description`, `click_action`, and `click_url` are ignored.
- The global catalog is cached for 60 minutes with a single-flight refresh, stale fallback, and a three-second bound shared by token acquisition and the request. The Mock profile registers a fixed source with `.invalid` image URLs and makes no network calls.
- The pane's load payload carries `badgesEnabled` (true when a badge source is registered) and `showBadges`. The pane fetches the catalog once per recording load, on its first open, in parallel with the emote fetch; not per message, reload, or poll.
- A stored badge whose set and version match renders an 18 px-high `<img>` in the chip's slot with `srcset`, `loading="lazy"`, `decoding="async"`, `referrerpolicy="no-referrer"`, and the catalog title as `alt` and `title` (with `info` appended to `title`). An unknown set or version keeps the existing text chip. An image error swaps that badge to its chip once and is not retried. A failed catalog fetch logs at `console.debug` and leaves the chips.
- "Show badges" (Settings › Chat) hides both badge images and text chips live; it is not a refetch or log rebuild.

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
- `SetAsync` validates the recording exists. For a finished recording it clamps to `[0, duration]` and clears positions within 30 s of the end. For a recording still being captured it only clamps the lower bound to 0: the live edge sits inside the 30 s margin and the library's duration trails the player's, so neither the margin nor the upper clamp applies. When the capture finishes, a position within 30 s of the final end is dropped (and so is one loaded at startup for a session recovered as finished), so the finished recording opens at 0. Unknown id is a no-op for in-process callers; the HTTP endpoint returns 404.
- `ClearAsync` removes the entry; delete also clears it.
- `Changed` fires per update; `ILibraryView` throttles resume-bar updates.
- `PUT /api/recordings/{id}/progress` is public while viewing is open (empty allowlist); in private mode it requires an allowed sign-in and answers `401` otherwise (ADR-0013). Body `{ positionSec }`; 204 on success; 400 for a non-finite or negative position; 404 for an unknown recording. `Cache-Control: no-store`.

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
    void Refresh();
}
```

- `DeleteAsync` requires an authenticated admin, only Complete sessions, takes `SessionStore.LockFor`, refuses while an export lease exists, removes the library entry, deletes the folder, clears progress, and releases the lease entry. Recording/Reconnecting throws `InvalidOperationException`; unknown id throws `KeyNotFoundException`.
- `StorageMonitor` samples `DiskGuard` every 60 s and after finalize/delete/export. `StorageView` is `(FreeBytes, TotalBytes)`; add `TotalBytes` to `DiskGuard` from `DriveInfo.TotalSize`. `Current` returns the latest successful sample, while `Changed` fires only when free space moves by more than 1% of total (or the total changes); sub-threshold samples still update `Current`. `IStorageMonitor.Refresh()` is an additive public method for post-finalize/delete/export sampling. Sampling and commit are serialized under one lock. Each sample is also judged against `Storage:MinFreeGb` with `DiskGuard.HasRoom`: crossing below logs one `Warning` and recovering logs one `Information`, each with `FreeGb`, `MinFreeGb` and `TotalGb` (#203). The state starts as "has room", so a first sample that is already low logs the warning once; repeated low samples log nothing.
- `ILibraryView.Storage` reads `StorageMonitor.Current`.

## 7. Authorization seams

| Service | Authorization |
|---|---|
| `ILibraryView` | Public read |
| `IProgressStore` | Public write while viewing is open; allowed sign-in in private mode (ADR-0013) |
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
