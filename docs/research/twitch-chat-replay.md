# Research memo: Twitch chat capture and synchronized replay

Status: research memo, 2026-11-13.
Issue: no dedicated issue. `gh issue list --search chat` returns only #131 (the EventSub conduits spike); #140 (EventSub reconnect handoff) is the other nearby item.
Binding: no. This memo informs a decision; it changes no contract. Adoption would amend `docs/session-store-contract.md`, `docs/player-contract.md`, `docs/frontend-foundation-contract.md`, `docs/backend-ui-contract.md`, `docs/hosting-contract.md`, `docs/testing-contract.md`, and optionally `docs/twitch-auth-contract.md`.

**Method.** Twitch facts come from official Twitch developer docs, the Twitch changelog, Twitch Developer Forums announcements/staff replies, FFmpeg master source and docs, Streamlink 8.6.3 docs/source, IRCv3, NuGet, and the Twitch Developer Agreement. All were fetched on 2026-11-13; links are in §12. No Twitch API was called and no credentials were used. The repo was read at `d47e4a0`. Serena's C# language server returned only the namespace for the inspected files, so code was read with `read`/`rg` (reported per `AGENTS.md`).

## 1. Summary and recommendation

**Recommendation: GO with a thin first slice, conditional on a one-hour anonymous-IRC prototype and two owner decisions.** Capture Twitch chat over IRC (anonymous by default, optional dedicated user token), persist it as per-session 10-minute JSONL buckets plus an events log, serve it through a public read-only range endpoint, and render it in a JS-owned sidecar pane to the right of the player. Synchronize with the first `#EXT-X-PROGRAM-DATE-TIME` of each part plus a stored `leadInSec` (default 12 s) and a per-recording user nudge. Do not use EventSub chat, VOD-chat GQL, SQLite, or TwitchLib in the first slice.

The recommendation deliberately accepts approximate sync. FFmpeg's `+program_date_time` writes the wall clock at the **HLS muxer start**, not the Twitch broadcast time of the media at t=0. Streamlink starts `Recorder:HlsLiveEdge` segments behind live (6 × ~2 s ≈ 12 s on Twitch). The mapping is therefore `mediaTime ≈ (chatWallClock − muxerStart) + leadInSec`, with an expected error of roughly ±2–5 s before the user nudge. It is not exact and must not be described as exact.

Conditions:

1. **Prototype anonymous IRC for ≤ 1 hour.** Current official docs require a user token with `chat:read` and no longer document `justinfan`; anonymous read-only access is widely reported working in 2026 but is undocumented. If it fails, the fallback is a dedicated `Chat:TwitchUserOAuthToken` with `chat:read`, not an admin-scope change.
2. **Owner decision: chat storage/retention vs the Twitch Developer Agreement.** The agreement's storage clause restricts storing Twitch Content beyond a 24-hour cache and requires deletion on request. Storing chat with recordings for public replay is a policy risk that needs an explicit owner decision; this memo does not give legal advice.
3. **Owner decision: accept approximate sync.** A per-recording nudge is the default correction; exact offsets require an unofficial VOD calibration or a recorder-pipeline change, both deferred.

## 2. What the repo already fixes — and one correction

### 2.1 Recorder and live edge

| Fact | Evidence |
|---|---|
| Streamlink starts `HlsLiveEdge` segments from the end of the source playlist (`edge_index = -(min(len(segments), max(int(live_edge), 1)))`). | `src/StreamKeepr/Infrastructure/Media/StreamlinkArgs.cs`; [hls.py:507][sl-hls] |
| `Recorder:HlsLiveEdge` defaults to 6; `Recorder:SegmentSeconds` defaults to 6. | `src/StreamKeepr/Configuration/RecorderOptions.cs:20,22` |
| Twitch HLS segments are ~2 s for most streams. With 6 segments, first recorded media is ~12 s behind live at recorder start. | [Streamlink Twitch plugin docs][sl-twitch] |
| FFmpeg writes fMP4 HLS event playlists with `+temp_file+independent_segments+program_date_time`. | `src/StreamKeepr/Infrastructure/Media/FfmpegArgs.cs:29`, `docs/recorder-contract.md:33-52` |
| The stitcher keeps each part's `#EXT-X-PROGRAM-DATE-TIME` and inserts `#EXT-X-DISCONTINUITY` between parts. | `src/StreamKeepr/Infrastructure/Storage/PlaylistStitcher.cs:80-90` |
| The stitcher already parses per-segment PDT for chapter placement. | `src/StreamKeepr/Infrastructure/Storage/PlaylistStitcher.cs:198-249`, `:118-152` |

### 2.2 Session layout and public media

- A session folder is `recordings/{channel}/{sessionId}/` and owns `session.json`, `index.m3u8`, part playlists, init segments, media segments, and `thumb.jpg` (`docs/session-store-contract.md:9,24-35`). Chat belongs inside that folder so delete/recovery/backup follow the recording.
- `/media` is public and allowlisted to `.m3u8/.m4s/.mp4/.jpg/.png`; `.json`, `.jsonl`, dot folders, and `.streamkeepr` are never served (`docs/hosting-contract.md:55-66`). Chat can therefore live under the session without a new media exposure; it is served only by an API endpoint.
- `RecordingLibrary` loads summaries from the session folder at startup and raises `Changed` (`src/StreamKeepr/Infrastructure/Storage/RecordingLibrary.cs:91-152`). A `chat/` subdirectory is ignored by the existing scan.
- `RecordingAdmin.DeleteAsync` deletes the whole session folder, so chat is deleted with the recording (`docs/backend-ui-contract.md:193`).

### 2.3 Player and shell ownership

- `VideoPlayer.razor` renders its skeleton once and returns `ShouldRender() => false`; `VideoPlayer.razor.js` owns hls.js, controls, progress, and dynamic DOM (`docs/player-contract.md:17-19`).
- The control order is binding: play, back, forward, volume, time, live edge, spacer, speed, quality, stats, PiP, theater, fullscreen (`docs/player-contract.md:65-80`). Adding a chat toggle there would require a contract amendment; the recommended design avoids it.
- The shell has a 1100 px main column, a 320 px drawer, theater hides the drawer, and breakpoints are 960/600 (`docs/frontend-foundation-contract.md:38-52`). The player is a 16:9 block (`src/StreamKeepr/wwwroot/css/components.css:227`) inside `.sk-main-inner` (`:409`).
- The player already uses DOM custom events for cross-module UI (`sk:stats-open`, `sk:stats-state`; `VideoPlayer.razor.js:488-499,816`), so a `sk:playhead` event fits the existing pattern.
- The player's global key handler ignores modifiers, editable targets, menus, dialogs, and slider keys (`docs/player-contract.md:107-111`); it does **not** currently ignore a generic chat scroll region. A chat pane must add that exclusion.

### 2.4 Correction: `StartedAt` is the broadcast start, not the recording start

The task brief describes `SessionManifest.StartedAt` as "when OpenSession ran." The code shows otherwise:

- `Session.Create` uses `live.StartedAt` (`src/StreamKeepr/Domain/Session.cs:24,31`), and `LiveStream.StartedAt` is Helix's `started_at` (`src/StreamKeepr/Infrastructure/Twitch/TwitchModels.cs:18`).
- `SessionStore.OpenAsync` writes `session.StartedAt` into the manifest (`src/StreamKeepr/Infrastructure/Storage/SessionStore.cs:76-83`).

So `StartedAt` is when the Twitch broadcast began, which can be hours before the recording. It is useful as metadata and for library ordering, but it is **not** a recording-start anchor. The recording-start signals are `DvrStatus.JobStartedAt(channel)` (per part, in memory only; `src/StreamKeepr/Infrastructure/Runtime/DvrStatus.cs:141`) and the FFmpeg muxer PDT (per part, on disk). The design uses the latter.

## 3. Capture options

### 3.1 Twitch IRC over WebSocket

| Question | Finding | Source |
|---|---|---|
| Endpoint / TLS | `wss://irc-ws.chat.twitch.tv:443`; plaintext WebSocket was decommissioned 2025-08-15. IRC TLS is `irc://irc.chat.twitch.tv:6697`. | [IRC concepts][irc], [changelog][changelog] |
| Official auth | `PASS oauth:<user-token>` then `NICK <lowercase-login>`; token needs `chat:read` (and `chat:edit` to send). | [IRC concepts][irc] |
| Anonymous read-only | `NICK justinfan<random>` with no real token; historically no PASS or a dummy PASS. **Not in current docs.** A 2024 developer-forum comment on Twitch's concurrent-join announcement said "Currently, IRC allows anonymous connections"; a 2025 forum reply said it "Should still work"; a 2024 forum reply notes justinfan "was never officially documented" and may be capped at 100 channels. | [2024 announcement/thread][join-limits], [2025 reply][justinfan-2025], [2024 reply][justinfan-undoc] |
| Capabilities | Request `twitch.tv/tags twitch.tv/commands`; tags requires commands. `twitch.tv/membership` is not needed (JOIN/PART noise). | [IRC concepts][irc] |
| Connection limits | 100 concurrent channel joins per user (since 2024); 20 join attempts per 10 s per user; 20 auth attempts per 10 s per user. Anonymous identity/limit behavior is **UNKNOWN**. | [Chat rate limits][chat-limits], [2024 announcement][join-limits] |
| Keepalive | Server sends `PING :tmi.twitch.tv`; reply `PONG :tmi.twitch.tv` or the connection is terminated. | [IRC concepts][irc] |
| Reconnect | `RECONNECT` means reconnect and rejoin; Twitch does not replay missed messages. IRC may also send duplicates and does not guarantee order. | [IRC concepts][irc] |
| Message framing | One WebSocket text frame may contain multiple CRLF-delimited IRC messages. | [IRC concepts][irc] |
| Size limits | IRCv3 caps the tag block at 8191 bytes and the rest of the line at 512 bytes; read frames up to 64 KB and split on CRLF. | [IRCv3 message tags][ircv3-tags] |
| Anonymous read scope | Anonymous users can read public chat; whether sub-only, followers-only, or verified-only rooms deliver messages to a justinfan connection is **UNKNOWN** (a logged-out web viewer can read sub-only chat, but this is not documented for IRC). Verify manually. | **UNKNOWN** |

**Tags and commands to capture.** `PRIVMSG` tags: `tmi-sent-ts` (Unix epoch **milliseconds** in every documented example), `id` (stable UUID; dedupe key), `room-id`, `user-id`, `display-name`, `color`, `badges`, `emotes`, `mod`, `subscriber`, `turbo`, `first-msg`, `reply-parent-*`, `bits`. `USERNOTICE` adds `msg-id`, `system-msg`, `msg-param-*`; `NOTICE` adds `msg-id`; `CLEARCHAT` has optional `target-user-id` and `ban-duration`; `CLEARMSG` has `target-msg-id`; `ROOMSTATE` has `subs-only`, `followers-only`, `emote-only`, `slow` ([IRC concepts][irc]).

**Escaping and Unicode.** Tag values use IRCv3 escaping: `\:` for `;`, `\s` for space, `\\` for backslash, `\r`, `\n`; split on `;` before unescaping ([IRCv3][ircv3-tags]). Emote ranges are zero-based **Unicode code point** indices, not UTF-16 code units; the Twitch docs do not state the unit, but this is the observed behavior and is required for emoji. `/me` messages are `\u0001ACTION <text>\u0001`; emote ranges start after `\u0001ACTION ` ([IRC concepts][irc], [forum evidence][emote-codepoints]). Verify with an emoji-plus-emote message before shipping.

**Assessment.** IRC is the only capture path that is independent of admin sign-in and works for arbitrary public channels without per-broadcaster authorization. Its weaknesses are that anonymous access is undocumented, message history is not replayed, and the 100-join limit is per user. Typical self-hosted deployments record a handful of channels, so the limit is acceptable for v1; a single multiplexed connection is preferred over one connection per session. Adding `chat:read` to the admin OAuth flow is a third option, but it couples chat capture to admin sign-in and changes the auth contract; do not take it in the first slice.

### 3.2 EventSub `channel.chat.message`

| Question | Finding | Source |
|---|---|---|
| Transports | Webhook, WebSocket, Conduit. | [EventSub chat reference][es-chat] |
| WebSocket auth | WebSocket subscriptions must use a user access token; app tokens fail. | [Manage subscriptions][es-manage] |
| User-token scope | `user:read:chat`; the reference's minimal bullet is a user token with that scope. | [EventSub chat reference][es-chat], [Chat auth][chat-auth] |
| App-token / conduit | Also needs `user:read:chat` + `user:bot` for the `user_id`, and `channel:bot` from the broadcaster, unless the `user_id` is a moderator. | [EventSub chat reference][es-chat] |
| Practical authorization | The authorized user must be allowed to read that channel's chat; Twitch's revocation statuses include `moderator_removed`, and app-token/conduit access is tied to broadcaster/moderator authorization. There is no app-only, anonymous, arbitrary-channel path. | [Manage subscriptions][es-manage], [Chat auth][chat-auth] |
| Cost | Chat subscription examples show `cost: 0`; subscriptions that require user authorization have no cost. | [EventSub chat reference][es-chat], [Manage subscriptions][es-manage] |
| Limits | 100 concurrent joins per user; 20 joins/10 s; WebSocket limits are 3 connections, 300 subscriptions, and cost 10 per user token. | [Chat rate limits][chat-limits], [Manage subscriptions][es-manage] |
| Clear/delete/notification | `channel.chat.clear`, `channel.chat.clear_user_messages`, `channel.chat.message_delete`, `channel.chat.notification`, `channel.chat_settings.update` exist and map to IRC `CLEARCHAT`/`CLEARMSG`/`USERNOTICE`/`ROOMSTATE`. | [EventSub chat reference][es-chat], [IRC migration][irc-migration] |

**Assessment: reject as the primary path.** It either requires a signed-in user token (violating "recording must not depend on admin sign-in") or `channel:bot` authorization from every broadcaster (which StreamKeepr cannot obtain for arbitrary channels). A dedicated chat user token is a possible fallback, but it is still a per-user, per-channel capability subject to the 100-join limit. Treat the WebSocket user as needing broadcaster/moderator standing for the channel until a test account proves broader access; the reference's single scope bullet is not enough to rely on.

### 3.3 VOD chat

There is **no official Helix endpoint for VOD chat comments**. `Get Videos` returns video metadata only, including `muted_segments`; it does not return chat ([Videos reference][api-videos]). The only practical source is Twitch's internal GQL operation `VideoCommentsByOffsetOrCursor`:

- Variables: `videoID` plus either `contentOffsetSeconds` (start offset) or `cursor`; persisted query hash `b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a` ([TwitchDownloader source][td-gql], [chat-downloader source][cd-gql]).
- Each returned node carries `contentOffsetSeconds`, the offset from the **VOD start**, not from StreamKeepr's recording; `createdAt`, `commenter`, and `message` are also present ([TwitchDownloader source][td-gql]).
- It is undocumented, can change without notice, requires a web Client-ID/hash, has unknown rate limits, and raises Twitch Developer Agreement questions. TwitchDownloader and chat-downloader are evidence that it works, not endorsed dependencies.

**Use only as an optional post-finalize calibration** if the owner accepts the risk; never as the primary capture path. It cannot provide live chat and fails when VOD chat is disabled or the VOD is deleted.

### 3.4 .NET libraries

| Option | Version / status | Fit | Verdict |
|---|---|---|---|
| Hand-rolled IRC over `ClientWebSocket` | Repo already hand-rolls EventSub (`src/StreamKeepr/Infrastructure/Twitch/ClientWebSocketEventSubSocket.cs`), uses `TimeProvider`, and has fake-socket tests. | ~300–500 lines for CAP/PASS/NICK/JOIN/PING/PONG/RECONNECT and tag parsing; no new dependency; fully testable offline. | **Recommended** |
| `TwitchLib.Client` | 4.0.2, published 2026-11-08, MIT, targets `net10.0`/`net9.0`/`net8.0`/`netstandard2.0/2.1`; repo active (pushed 2026-01-15). | The issue example uses `ConnectionCredentials("justinfan…", "")`; verify anonymous login and reconnect on 4.0.2. Adds a dependency and its own connection model. | Alternative if the owner prefers a library |
| `TwitchLib.EventSub.Websockets` | Separate package; not needed for this feature. | Same authorization problem as EventSub chat. | Reject for v1 |

The repo rule is "no new dependency without owner approval." A small client consistent with the existing EventSub socket is the lower-risk choice; TwitchLib is a reasonable fallback if the owner wants less protocol code.

### 3.5 Policy and privacy

The Twitch Developer Services Agreement (last modified 2024-12-04), Schedule 1, §C "Storage of Program Materials/Twitch Content," says:

> "Do not store copies of Twitch Content or Program Materials, unless you: (a) obtain prior written authorization from Twitch (through these terms or otherwise); (b) control the rights associated with such content; or (c) cache such information for only a twenty-four hour time period without further sharing it with third parties."

It also requires deleting an end user's data on termination, revocation, reduced scope, or request, and honoring Twitch deletions/expiry ([Developer Agreement][dev-agreement]). This is a material risk for indefinite public chat replay; it is not a legal conclusion. Chat also contains personal data (display name, user ID, message text). Mitigations: store only what the pane needs, honor `CLEARMSG`/`CLEARCHAT`, provide a retention setting and deletion path, avoid logging message text/tokens, and consider an access-controlled mode. The pane displays user-generated text verbatim; there is no moderation/filtering in v1, so a public instance should consider an access-control or hide-chat option. The owner should decide retention and public exposure with advice; this memo does not resolve it.

## 4. Timing and synchronization

### 4.1 What FFmpeg `+program_date_time` actually writes

`libavformat/hlsenc.c` initializes `initial_program_date_time` with `av_gettime() / 1000000.0` in `hls_init()`, and `libavformat/hlsplaylist.c` writes `#EXT-X-PROGRAM-DATE-TIME` from that value and then advances it by each segment's duration ([hlsenc.c:2908][hlsenc], [hlsplaylist.c:189-190][hlsplaylist]).

Therefore:

- PDT is **wall-clock time at the HLS muxer start**, plus the sum of output segment durations.
- It is **not** the input PTS and **not** the Twitch broadcast time of the media at t=0.
- For a part whose first segment starts at media time 0, the content in that segment aired roughly `leadInSec` before the PDT wall clock.
- The tag includes a UTC offset (`%z`), so parsing it yields an absolute instant even if the container's local timezone is not UTC.

FFmpeg's docs only say the flag "Generate[s] EXT-X-PROGRAM-DATE-TIME tags" ([FFmpeg formats][ff-formats]); the semantics above come from the source.

### 4.2 What Streamlink `--hls-live-edge` actually does

`HLSStreamWorker.process_segments()` sets:

```python
edge_index = -(min(len(segments), max(int(self.live_edge), 1)))
edge_segment = segments[edge_index]
self.sequence = edge_segment.num
```

([hls.py:507-508][sl-hls]). `--hls-live-edge` is a **segment count**, not seconds; the default is 3 ([Streamlink CLI][sl-cli]). Twitch HLS segments are "2 seconds for most streams" ([Streamlink Twitch docs][sl-twitch]). StreamKeepr sets 6, so the first recorded segment starts about 12 s before the live playlist end at recorder start.

Streamlink writes the raw segment payload to stdout (`_write_plain` → `iter_segment_content`; [hls.py:339-374][sl-hls]). It does **not** forward playlist tags such as `#EXT-X-PROGRAM-DATE-TIME` or `#EXT-X-TWITCH-ELAPSED-SECS`; those are metadata on the source playlist, not bytes in the segment. It does preserve in-band PTS for plain TS/fMP4 segments, but the recorder's FFmpeg default timestamp handling shifts output timestamps to start at zero, and hlsenc's PDT ignores PTS anyway. The Streamlink docs note that fMP4 streams have a non-zero presentation-time offset pointing to the broadcast start, but that offset is not captured by the current recorder pipeline ([Streamlink Twitch docs][sl-twitch]).

### 4.3 Mapping options and expected error

Let `M` be the first PDT of a part (the muxer start), `L` the stored `leadInSec`, and `W` the chat message wall clock (`tmi-sent-ts` ms). The per-part mapping is:

```text
mediaTime(W) = (W - M) / 1000 + L
```

| Strategy | What it uses | Exact? | Expected error | Verdict |
|---|---|---|---|---|
| PDT only | First `#EXT-X-PROGRAM-DATE-TIME` | Exact for the muxer clock, wrong for content | ~12 s + startup/probe (10–16 s) | Reject |
| PDT + configured live edge (`L=12`) | PDT + `HlsLiveEdge × 2 s` | Approximate | ±2–5 s (segment rounding, playlist refresh, Streamlink resolve/download, FFmpeg probe) | **Default** |
| `SessionManifest.StartedAt` + live edge | Twitch `started_at` | Wrong anchor | Hours, because `StartedAt` is broadcast start | Reject |
| `DvrStatus.JobStartedAt` + live edge | Per-part job start (in memory) | Approximate, not persisted | ±3–8 s (resolve/download/probe) | Not durable; use only as a sanity bound |
| Source playlist `EXT-X-TWITCH-ELAPSED-SECS` | Sidecar `streamlink --stream-url` + GET | Potentially exact to ~1–2 s | Race with Streamlink's start; needs a second resolution | Deferred; possible calibration |
| VOD `contentOffsetSeconds` | GQL after finalize | Exact relative to VOD | ±1 s after id-based alignment; unofficial | Optional calibration |
| User nudge | Stored per recording | User-perceived | ±1 s | **Required correction UX** |

The expected error is dominated by the live-edge assumption, not by the PDT clock. `leadInSec` must be stored per session because changing `Recorder:HlsLiveEdge` later would otherwise silently shift old recordings. Treat the ranges above as estimates: measure the first ten sessions with a known message/time pair (or a hand-timed “now” chat message) before publishing a default.

### 4.4 Recommended algorithm

1. At session start, store `leadInSec` from `Chat:LeadInSeconds` (default 12, range 0–60) in `chat/meta.json`. Log a warning if it differs from `HlsLiveEdge × 2`; do not auto-change it.
2. For each part `pNNN.m3u8`, read the first `#EXT-X-PROGRAM-DATE-TIME` (`M`) and the sum of `#EXTINF` durations through the last segment URI (`D`, the stitcher's rule drops a trailing `#EXTINF` without a URI; `PlaylistStitcher.cs:154-172`). Compute `mediaStart` as the cumulative duration of earlier parts. The stitched index preserves the same order and durations, so this matches playback.
3. Content window for the part: `[M − L, M − L + D]`. A chat message at `W` belongs to the part whose window contains it.
4. Compute `mt = mediaStart + (W − (M − L)) / 1000`; clamp to `[mediaStart, mediaStart + D]`.
5. If a message is between parts (no window contains it), skip it and report a gap; do not place it at a misleading boundary. If two parts overlap because a restart rewound to the live edge, assign the message to the earliest part and dedupe by `id`.
6. Apply `syncOffsetSec` (positive = show later) after step 4, clamped to ±30 s, and clamp to the part window.
7. For live sessions, return `state: "starting"` until the first part's PDT exists; the player is still waiting for its first segment then, so the pane can show a placeholder.

### 4.5 Gaps, reconnects, part overlap

- **Part gap:** the wall-clock gap between part content windows is unrecorded media. Chat during it is skipped; the endpoint returns `gaps` so the pane can render “Chat unavailable for …”. The media timeline stays contiguous because the stitcher concatenates parts.
- **Part overlap:** if a restart begins less than `L` after the previous part's content end, the next part's content window overlaps the previous one. Assign messages to the earliest part and dedupe by `id`; never show the same message twice.
- **IRC reconnect:** Twitch does not replay missed messages. Write a `gap` event from the last captured `at` to the rejoin time; the pane shows a divider.
- **Recorder gap while chat stays connected:** messages have no part window; skip and report a gap. Do not stretch the media timeline.
- **Per-part PDT:** each part has its own `M`; do not assume the stitched timeline has one monotonic PDT. The stitcher keeps per-part tags and a discontinuity, so per-part anchors are the correct unit.

### 4.6 Correction UX

A small sync control in the pane footer: `−2 s`, `−1 s`, `Reset`, `+1 s`, `+2 s`, showing the current offset. Persist `{"v":1,"offsets":{"<recordingId>":1.5}}` in `localStorage` under `sk-chat`; clamp to ±30 s and cap at 100 recordings. This is a viewing preference, not a secret, and does not need a server write. A per-recording server-side offset would invite abuse and add an endpoint; don't.

Optional later: after finalize, if the VOD exists, fetch GQL comments and align live-captured IDs to `contentOffsetSeconds`; the median difference is a calibration offset. Store it as `calibratedOffsetSec` in `meta.json` but keep the user nudge on top. This is unofficial and must fail silently.

## 5. Persistence

### 5.1 Options

| Option | Query by media window | Crash safety | Size for 12 h | Dependencies | Verdict |
|---|---|---|---|---|---|
| One JSONL per session + sparse index | Binary search by `at`, scan a window | Partial trailing line ignored; index rebuildable | 95 MB at 10 msg/s, 475 MB at 50 msg/s (≈220 B/line) | None | Good fallback |
| 10-minute UTC JSONL buckets + `events.jsonl` + `meta.json` | Read 1–2 buckets per window; bounded | Per-bucket append; partial line ignored; meta atomic | Same total, ~1.3 MB at 10 msg/s to ~6.6 MB at 50 msg/s per bucket | None | **Recommended** |
| SQLite (`Microsoft.Data.Sqlite`) per recording | Excellent (indexed `at`, `id`, delete flags) | WAL/transactions | Similar + DB overhead | New package, schema/migration, backup/delete complexity | Reject v1 |
| Fixed 5-minute files | Similar to 10-minute buckets but more files | Same | Same | None | 10-minute is enough |

The repo has no database and its persistence pattern is plain files with temp+rename (`progress.json`, `session.json`; `docs/session-store-contract.md:53-59`). Buckets fit that posture and avoid a new dependency.

### 5.2 Proposed layout

```text
recordings/{channel}/{sessionId}/
  session.json
  index.m3u8
  p001.m3u8, init_001.mp4, p001_00000.m4s, ...
  chat/
    meta.json
    events.jsonl
    20261113T1800Z.jsonl      # 10-minute UTC bucket, 18:00:00–18:09:59.999
    20261113T1810Z.jsonl
    ...
```

- Bucket names are UTC and lexicographically sortable; no colons (Windows-safe).
- `chat/` is inside the session folder, so `DeleteAsync` removes it, backup copies it, and recovery ignores it.
- The media allowlist never serves `.json`/`.jsonl`; the API is the only chat surface (`docs/hosting-contract.md:55-66`). Add `chat/` to the “never served” list in the hosting contract for clarity.

### 5.3 Schema (v1)

`chat/meta.json` (atomic temp+rename; advisory, session manifest is authoritative):

```json
{
  "v": 1,
  "sessionId": "20261113T180000Z_123456789",
  "channel": "example",
  "source": "irc-anonymous",
  "status": "recording",
  "startedAt": "2026-11-13T18:00:00.000Z",
  "endedAt": null,
  "leadInSec": 12,
  "bucketSeconds": 600,
  "lastMessageAt": "2026-11-13T18:42:11.123Z",
  "messageCount": 12345,
  "truncated": false,
  "lastError": null
}
```

`status` is `recording`, `complete`, or `unavailable`; `source` is `irc-anonymous` or `irc-token`. Do not store a token or login password.

Message line (`chat/YYYYMMDDTHHmmZ.jsonl`):

```json
{"v":1,"id":"b34ccfc7-4977-403a-8a94-33c6bac34fb8","at":1763047200123,"uid":"1337","login":"ronni","display":"Ronni","color":"#0D4200","badges":[["turbo","1",""]],"text":"Kappa Keepo Kappa","emotes":[{"id":"25","ranges":[[0,4],[12,16]]},{"id":"1902","ranges":[[6,10]]}],"bits":null,"reply":null,"action":false}
```

- `at` is `tmi-sent-ts`, Unix epoch **milliseconds**. `mt` is computed at query time, never stored.
- `id` is the Twitch message UUID; the dedupe key.
- `uid` is the Twitch user ID. It is personal data; store it only if deletion-by-user needs it. A minimization option is to omit `uid` and use `login` for `CLEARCHAT` matching (less reliable if a login changes).
- `badges` is `[set-id, version, info]`; `info` is the subscriber-month metadata, optional.
- `emotes` ranges are zero-based Unicode code point indices, `[start, end]` inclusive, as Twitch sends them. `action` is true for `/me`; `text` is the stripped message body.
- `reply` is `null` or `{"id","login","display","text"}` from `reply-parent-*`; truncate `text` to 500 characters.
- `bits` is `null` or an integer from the `bits` tag.
- Shared-chat fields (`source-*`) are not stored in v1; if needed later, add `sourceId`, `sourceRoomId` with a `v` bump.

Event line (`chat/events.jsonl`):

```json
{"v":1,"kind":"delete","at":1763047200456,"targetId":"..."}
{"v":1,"kind":"clear_user","at":1763047201456,"targetUid":"1337","targetLogin":"ronni"}
{"v":1,"kind":"clear_all","at":1763047202456}
{"v":1,"kind":"gap","at":1763047203456,"from":1763047200000,"to":1763047260000,"reason":"irc-disconnected"}
{"v":1,"kind":"end","at":1763047260000}
```

- `delete` applies by `targetId` regardless of time; `clear_user` applies to that user's messages with `at <= event.at`; `clear_all` applies to all messages with `at <= event.at`.
- `delete`/`clear` `at` is the Twitch `tmi-sent-ts`; `gap`/`end` `at` is the local capture wall clock. The reader uses the message `at` for ordering and the event `at` only for the clear cutoff.
- `gap` marks a capture outage or a recorder gap; `end` is written on finalize/shutdown (meta is still authoritative).
- Events are small relative to messages (moderation actions are rare), so reading the whole file per query is acceptable in v1; add a tail index if a channel proves exceptional.

### 5.4 Write, read, crash, and deletion semantics

- **One writer per session.** `ChatCaptureService` owns a `StreamWriter`/`FileStream` over the current bucket, `FileShare.Read`, flush every 1 s or 100 messages. A 1–2 s reorder buffer sorts by `at` before appending; tie-break `id`.
- **Bucket rotation.** Assign by `at` to a 10-minute UTC bucket. Keep the current and previous bucket open so a message that arrives a few seconds late still lands in the correct bucket. If a message is more than one bucket late (unlikely), write it to the current bucket; readers scan one margin bucket on each side of the requested range.
- **Duplicate suppression.** Keep a bounded `HashSet<string>` of recent IDs in the writer; on restart, seed from the current bucket's tail. The reader also dedupes by `id`, so duplicates are never shown.
- **Alignment.** A reader takes a byte snapshot to the current EOF, splits on `\n`, ignores a trailing partial line, ignores malformed lines with a warning, and never fails the request because of one bad line. Cap line length at 1 MB.
- **Crash safety.** A crash can lose the last unflushed second and leave a partial line; both are acceptable. `meta.json` is temp+rename; if stale, derive live/complete from `session.json` and `RecordingSummary.IsRecording`.
- **Deletion.** `CLEARMSG`/`CLEARCHAT` are stored as events and filtered at query time. This honors deletion in the UI but leaves the original text on disk; note that in the privacy section. A later compaction pass can rewrite buckets without deleted messages, but it is not in v1.
- **Recording deletion.** The existing delete removes the session folder, including `chat/`. No extra cleanup.
- **Disk full.** Chat writes are best-effort. If free space drops below `Storage:MinFreeGb` or `Chat:MaxBytesPerSession` (default 512 MiB) is reached, stop capture, write a `gap`/`truncated` marker, log once, and never affect recording. The recorder's `DiskGuard` remains the recording gate.
- **Backup/export.** Folder backup includes chat. MP4 export does not include chat; a separate export is deferred.

### 5.5 Query algorithm

Given `fromSec`, `toSec`, and `syncOffsetSec`:

1. Load `meta.json`; if absent, return `hasChat:false`.
2. Build the part timeline: for each `pNNN.m3u8`, first PDT `M`, duration `D`, media start `S`. Cache it by the last part playlist write time (the same invalidation idea as `StitchLiveAsync`, `src/StreamKeepr/Infrastructure/Storage/SessionStore.cs:170-201`). `ReadPartsAsync` is private today; add a read-only `SessionStore.ReadPartTimingsAsync` under `LockFor`, or reuse `PartFiles`/`PartPlaylist` from `ChatTimeline`. Do not reimplement part numbering or the `p{part:D3}` rule.
3. Intersect `[fromSec,toSec]` with each part's `[S,S+D]`; convert the intersection to a wall-clock range `[M−L+(from−S), M−L+(to−S)]`. Merge overlapping ranges.
4. Read the 10-minute buckets overlapping each range, plus one margin bucket on each side.
5. Parse messages; keep those whose `at` is inside a part window and whose `id` is not already seen. Compute `mt` with the part that contains `at`; on overlap, the earliest part wins.
6. Apply events (delete/clear) and `syncOffsetSec`; clamp `mt` to the part window.
7. Sort by `(mt, id)`, apply `limit`, return `truncated`/`nextFrom`.

### 5.6 HTTP endpoint

```http
GET /api/recordings/{id}/chat?fromSec=120.5&toSec=300&limit=300&syncOffsetSec=0
```

- Public, like playback/progress (`docs/hosting-contract.md:35-48`). `404` unknown recording; `400` non-finite, negative, `toSec <= fromSec`, window > 600 s, or `limit` outside 1–1000; `200` with an empty list when chat was not captured.
- `Cache-Control: no-store`; chat changes while live and deletions must be immediate.
- Response:

```json
{
  "v": 1,
  "recordingId": "20261113T180000Z_123456789",
  "state": "recording",
  "hasChat": true,
  "source": "irc-anonymous",
  "leadInSec": 12,
  "parts": [{"part":1,"pdt0":"2026-11-13T18:00:00.000Z","mediaStart":0,"duration":3600}],
  "gaps": [{"from":3600,"to":3660}],
  "messages": [
    {"id":"...","mt":123.456,"at":1763047200123,"uid":"1337","login":"ronni","display":"Ronni","color":"#0D4200","badges":[["turbo","1",""]],"text":"Kappa","emotes":[{"id":"25","ranges":[[0,4]]}],"bits":null,"reply":null,"action":false}
  ],
  "truncated": false,
  "nextFrom": 300.0
}
```

`state` is `recording`, `complete`, `unavailable`, or `starting` (live but no part PDT yet). `hasChat` mirrors the proposed `RecordingSummary.HasChat`. `syncOffsetSec` is applied server-side; the pane refetches a small window after a nudge (debounced 150 ms). Do not also shift the rendered rows by the same delta, or the offset is applied twice.

### 5.7 Size and retention

At ~220 B/message, 12 h is ~95 MB at 10 msg/s and ~475 MB at 50 msg/s; a 10-minute bucket is ~1.3 MB at 10 msg/s and ~6.6 MB at 50 msg/s. Retain chat with the recording by default (the repo has no retention service; ADR-0004 makes retention a non-goal). A `Chat:MaxBytesPerSession` cap (512 MiB) and the existing disk guard prevent chat from crowding out media. Compression per bucket at finalize is a later optimization; it would make range reads decompress a whole bucket and is not worth the complexity in v1.

## 6. Display and UX

### 6.1 Layout options

| Option | Behavior | Fit | Verdict |
|---|---|---|---|
| Fixed sidecar | Player and chat share a row; chat 320–340 px; stacks below the player < 960 px | Keeps both visible; matches drawer width; player shrinks but remains usable | **Recommended** |
| Overlay/drawer | Chat slides over the player's right edge | Does not shrink video, but covers content and fights fullscreen/theater | Reject |
| Tab strip | Chat as a tab below the player | No player shrink, but not “to the right” and less glanceable | Reject |

### 6.2 Recommended sidecar

```text
.sk-main-inner
  .sk-player-row (grid: minmax(0,1fr) 320px)
    [data-slot="player"]  VideoPlayer
    ChatPane
  [data-slot="details"]
  [data-slot="stats"]
```

- At ≥ 960 px: two columns, chat 320 px (clamp 280–400), gap 12 px. The player remains 16:9; the page scrolls normally. Use a container query on `.sk-player-row` (stack below ~880 px row width) rather than viewport width alone: at 960–1200 px with the drawer expanded, the row can be too narrow for a usable player even though the viewport is above 960. In that range, default the pane collapsed.
- At < 960 px: one column; the chat pane sits below the player, full width, max-height 50vh; default collapsed. The drawer is already modal there, so no extra overlay.
- At < 600 px: same stacked layout; the pane header is a full-width bar.
- Theater: chat stays visible; theater only hides the drawer (`docs/frontend-foundation-contract.md:48`). Fullscreen targets `.sk-player`, so chat is outside fullscreen; it remains open when fullscreen exits. Document this.
- Width: keep `main` at 1100 px by default. On wide screens with chat open, allow the recording main column to grow to 1280 px so the player is ~948 px instead of ~768 px (1280 − 320 − 12). This is a shell contract amendment; the library stays at 1100 px. The 1280 px cap only helps when the viewport is wide enough after the drawer; with the drawer expanded at 320 px, that means roughly ≥ 1600 px, and with the drawer railed it means roughly ≥ 1370 px.

### 6.3 Toggle, keyboard, and persistence

- **Pane header**, not the player control bar. The player control order is binding (`docs/player-contract.md:65-80`); adding a chat button there would require an amendment and a design pass. A header button with `aria-expanded`/`aria-controls` is sufficient.
- When collapsed on desktop, leave a 44 px vertical rail with a chat icon and an unread dot; on mobile, a full-width header bar. The rail keeps the feature discoverable without a control-bar change.
- Keyboard shortcut `C` toggles the pane. It is not used by the player (Space/K, J/L, arrows, M, F, T, I; `docs/player-contract.md:107-111`). Ignore `C` in inputs, textareas, selects, and `[contenteditable]`.
- Persist `ChatOpen` in `UiPreferences` / the versioned `sk-ui` cookie, default `false`. The cookie schema adds `"chat":false`; unknown fields are already ignored, and the 4 KB/50-entry caps remain (`docs/frontend-foundation-contract.md:71-76`). This is layout state, so `sk-ui`, not localStorage.
- The pane's sync offset is a viewing preference in `sk-chat` localStorage (§4.6), separate from layout state.
- The player's global key handler must ignore events whose target is inside `[data-sk-chat]`, especially Space/arrows/Home/End, so scrolling chat does not play/pause or seek the video. Add this to `docs/player-contract.md` and the Vitest suite.

### 6.4 Rendering ownership

- `ChatPane.razor` renders the skeleton once: `<aside>`, header/toggle, empty log container, status region, sync footer. It returns `ShouldRender() => false` after the first interactive render, like the player and stats panel. JS applies `ChatOpen` immediately (ARIA, rail/width) and calls .NET to persist it; `UiPreferences.ChatOpen` supplies the initial state on reload. Message updates never re-render it.
- `ChatPane.razor.js` owns the message DOM, fetch/poll loop, reveal queue, auto-scroll, new-message count, and sync controls. Blazor does not re-render messages.
- Why not Blazor `Virtualize`: messages arrive more than once per second and the reveal is tied to `currentTime`; a circuit round-trip per timeupdate violates the “more than once a second stays in JS” rule (`docs/frontend-foundation-contract.md:142`). Variable-height messages also make `Virtualize` awkward. Use a rolling window of 200–300 rendered rows; prune from the top and fetch on seek. This is simpler and fast enough for a 2–5 minute window.
- `RecordingView.HasChat` gates the pane. If false, render no toggle/pane. The endpoint's `state` handles “captured but unavailable” and “starting.”

### 6.5 Playhead seam

The player owns the `<video>`; the chat pane must not own a second media element. Add a throttled DOM event in `VideoPlayer.razor.js`:

```js
root.dispatchEvent(new CustomEvent("sk:playhead", {
  detail: { recordingId, time: video.currentTime, duration, paused: video.paused, rate: video.playbackRate, live },
  bubbles: true,
}));
```

- Dispatch on `timeupdate` at most every 250 ms, and immediately on `play`, `pause`, `seeked`, `ratechange`, `ended`, `loadedmetadata`, and `durationchange`.
- The chat module listens on `document`, filters by `recordingId`, and never calls .NET per tick.
- Why not read `video.currentTime` directly: the player may replace the media element on `load`/native fallback; the event keeps the player as the owner and matches the existing `sk:stats-*` pattern. A direct read is a fallback if the event is not ready.
- Optional `sk:seek` event from chat to player for clicking a message's timestamp; defer if it complicates the player contract.

### 6.6 Message rendering

- **Usernames/colors:** use the `color` tag if it matches `^#[0-9A-Fa-f]{6}$`; compute contrast against the pane background and fall back to `--on-surface` below 4.5:1. Always render the name text; never rely on color alone.
- **Badges:** v1 renders text chips (`MOD`, `SUB`, `VIP`, `BROADCASTER`, `STAFF`) from a small map, with `title` and an accessible label. Badge images need Helix global/channel badge calls and a cache; defer. Store raw badges so images can be added later.
- **Native Twitch emotes:** build `<img>` from the official template `https://static-cdn.jtvnw.net/emoticons/v2/{{id}}/{{format}}/{{theme_mode}}/{{scale}}` ([Chat API reference][api-chat], [Handling emotes][emote-cdn]). Use `scale` 1.0/2.0/3.0 in `srcset`, `format=static` (or `animated` when a Helix lookup says the emote supports it; the IRC tag does not carry the format), `theme_mode=dark` (or match the pane theme), `alt` = the original text, `title` = the original text, `loading="lazy"`, `decoding="async"`. Validate the emote ID (`^[A-Za-z0-9_]+$`); never interpolate raw text into a URL.
- **Replies:** render a one-line quote with the parent display name and truncated text above the message; clicking scrolls to the parent if it is in the DOM. Store the parent fields from `reply-parent-*`.
- **Actions:** strip `\u0001ACTION ` and the trailing `\u0001`, set `action:true`, render italic with the user's name.
- **Mentions:** leave as text in v1; do not parse or linkify. This avoids XSS and mention-spam styling.
- **Sub/raid notices:** `USERNOTICE` is **deferred**; v1 ignores it. If added later, render `system-msg` as a system line, not as a user message.
- **Deleted messages:** the API omits deleted messages; if a delete arrives while one is displayed, remove its node. Do not show “message deleted” unless the owner asks.
- **Gaps:** render a divider “Chat unavailable for 2m 10s” from the `gaps` array or a `gap` event. Announce reconnects once via the polite status region.
- **Links:** render message text as text, not clickable, in v1. If linkifying later, use `rel="noopener noreferrer nofollow"` and a URL allowlist.
- **XSS:** all text via `textContent`; no `innerHTML`; validate colors, emote IDs, and badge IDs; cap text length.

### 6.7 Live behavior

- The player's live edge is about one target duration (12 s with the current `SegmentSeconds`) behind the playlist end (`docs/player-contract.md:54-57`). Chat arrives in real time, so it is ahead of the playhead by the recorder's lead-in plus the player's live-edge buffer. The pane must reveal by **media time**, not arrival time.
- While `state=recording`, poll the endpoint every 1 s with a 5 s overlap (`fromSec = loadedEnd - 5`), dedupe by `id`, and append to the pending queue. Poll only while the pane is open, the document is visible, and playback is not paused; on resume, fetch the overlap once.
- On each `sk:playhead`, pop pending messages with `mt <= time + 0.5`. This automatically follows play/pause/seek/speed because it uses `currentTime`, not wall time.
- On a backward seek > 2 s or a forward jump > 120 s, abort the in-flight fetch, clear the pending queue and DOM, fetch `[time-30, time+120]`, and render the window around the new position.
- Auto-scroll: pinned if the user is within 24 px of the bottom; otherwise unpin, count new messages, and show “N new messages / Jump to current time.” The button scrolls to the bottom and resets the count. Do not force-scroll while the user is reading.
- “Jump to live” belongs to the player; the chat pane's “Jump to current time” scrolls chat to the playhead. When finished, no live button.
- 1 s polling is invisible at a 12 s player latency. SSE is a later optimization if the pane needs sub-second updates; it adds proxy-buffering and connection-management risk.

### 6.8 Accessibility

- The log is `role="log"` with `aria-live="off"`; do **not** announce every message. A separate `role="status" aria-live="polite"` announces “Chat connected”, “Chat unavailable”, and “N new messages” (throttled), not message text.
- The log is `tabindex="0"`, labelled “Chat replay”; PageUp/PageDown/Home/End scroll it. Do not trap focus. The toggle is a button with `aria-expanded` and `aria-controls`; focus stays on the toggle when opening/closing.
- Messages are `<li>` in an `<ol>`; each item's accessible text is “Display: message”; emote `alt` is the emote name; timestamps are visually hidden or exposed as `aria-label` (“at 1:23”).
- Reduced motion: no smooth scroll, no fade/pulse; use instant `scrollTop`.
- Forced colors: `@media (forced-colors: active)` uses `Canvas`/`CanvasText`, badge chips have a border, and user colors fall back to `CanvasText`.
- Contrast: clamp user colors; never use color as the only cue for role/badge.
- The pane is not a modal; `Escape` remains owned by `shortcuts.js` (the player never handles it). Do not add an `Escape` handler in the chat pane; if clearing search while chat is focused is undesirable, add `[data-sk-chat]` to `shortcuts.js`'s editable-target guard as a follow-up.

## 7. End-to-end options

| Option | Capture | Persistence | UI | Effort | Dependencies | Main risks | Verdict |
|---|---|---|---|---|---|---|---|
| **A. Anonymous IRC + JSONL buckets + sidecar** | Hand-rolled IRC over `ClientWebSocket`; optional token fallback | 10-min UTC buckets + `events.jsonl` + `meta.json` | JS-owned sidecar, `sk:playhead`, `sk-ui` chat | 6–8 days | None | Undocumented IRC, ±2–5 s sync, ToS/retention | **Recommended** |
| B. EventSub chat + user token | `channel.chat.message` over WebSocket with a user token | Same | Same | 6–9 days + auth changes | None | Per-channel authorization, admin dependency, 100-join limit | Reject as primary; possible fallback |
| C. VOD GQL backfill | GQL `VideoCommentsByOffsetOrCursor` after finalize | Same | Same, but post-stream only | 4–6 days | None (HTTP) | Unofficial GQL, no live chat, VOD availability | Defer to calibration |
| D. SQLite | IRC | One DB per recording or one DB | Same | 8–10 days | `Microsoft.Data.Sqlite` | New dependency, migrations, backup/delete | Reject v1 |
| E. Embed Twitch chat | Twitch iframe | None | Iframe | 1–2 days | External scripts | No sync/replay, auth/consent, no own data | Reject |

**Recommended: A.** It is the only option that captures arbitrary channels, does not require admin sign-in, adds no package, and fits the repo's file-based persistence and JS-owned player. B is technically cleaner data but fails the authorization constraint. C is a useful post-finalize calibration, not a primary path. D is overkill for 10-minute buckets. E cannot synchronize or replay.

## 8. Implementation outline

### 8.1 Files and responsibilities

| Area | File | Responsibility |
|---|---|---|
| Config | `src/StreamKeepr/Configuration/ChatOptions.cs`, `src/StreamKeepr/appsettings.json` | `Enabled` (default false), `LeadInSeconds` (12, 0–60), `TwitchUserOAuthToken` (optional secret), `TwitchLogin` (optional), `MaxBytesPerSession` (512 MiB); add a `Chat` section to `appsettings.json` |
| IRC transport | `src/StreamKeepr/Infrastructure/Twitch/ChatIrcClient.cs` | `ClientWebSocket` connect, CAP/PASS/NICK/JOIN, PING/PONG, RECONNECT, backoff, join queue, per-channel message routing; `IChatIrcClient` for fakes |
| Parser | `src/StreamKeepr/Infrastructure/Twitch/ChatMessageParser.cs` | Pure IRCv3 tag/prefix/command parser; PRIVMSG/CLEARMSG/CLEARCHAT/NOTICE/ROOMSTATE/RECONNECT/PING; escaping; ACTION; code-point emote ranges; validation |
| Capture | `src/StreamKeepr/Infrastructure/Twitch/ChatCaptureService.cs` | `BackgroundService`; reconciles `RecordingLibrary` + `DvrStatus.ActiveJob` every 1 s; starts/stops per session; writes buckets/events/meta; token validation; never throws into recording |
| Store | `src/StreamKeepr/Infrastructure/Storage/ChatLogStore.cs` | Bucket writer/reader, `events.jsonl`, `meta.json`, atomic meta, disk cap, partial-line handling |
| Reader | `src/StreamKeepr/Infrastructure/Storage/ChatReader.cs` | Range query, bucket selection, dedupe, tombstones, ordering, limit/cursor |
| Timeline | `src/StreamKeepr/Infrastructure/Storage/ChatTimeline.cs` | Part PDT/duration/media-start anchors; media↔wall mapping; gaps; cache invalidation |
| Endpoint | `src/StreamKeepr/Hosting/ChatEndpoint.cs` | `GET /api/recordings/{id}/chat`, validation, `no-store`, 400/404 |
| UI shell | `src/StreamKeepr/Components/Features/Chat/ChatPane.razor` | Skeleton, header/toggle, log container, status, sync footer; `ShouldRender() => false`; JS owns the toggle/log, .NET persists |
| UI JS | `src/StreamKeepr/Components/Features/Chat/ChatPane.razor.js` | Fetch/poll, reveal queue, DOM, auto-scroll, nudge, `sk:playhead` listener |
| Player seam | `src/StreamKeepr/Components/Features/Player/VideoPlayer.razor.js` | Throttled `sk:playhead`; ignore keys inside `[data-sk-chat]` |
| Preferences | `src/StreamKeepr/Ui/UiPreferences.cs` | `ChatOpen` + `sk-ui` `"chat"` field |
| Summary | `src/StreamKeepr/Infrastructure/Storage/RecordingSummary.cs`, `src/StreamKeepr/Ui/LibraryView.cs` | `HasChat` derived from `chat/meta.json` |
| Wiring | `src/StreamKeepr/Program.cs` | `ChatOptions` validation, DI, `MapChatEndpoint`, hosted service |
| Mock | `src/StreamKeepr/Mock/MockChatFeed.cs`, `MockSeeder` | Deterministic chat buckets for the growing and finished fixtures; fake chat client |
| Tests | `tests/StreamKeepr.Tests/Twitch/ChatMessageParserTests.cs`, `.../ChatCaptureServiceTests.cs`, `.../Storage/ChatLogStoreTests.cs`, `.../Storage/ChatTimelineTests.cs`, `.../Hosting/ChatEndpointTests.cs`, `.../Ui/ChatPaneTests.cs`, `tests/js/chat-pane.test.js`, `tests/StreamKeepr.E2E/ChatJourneyTests.cs` | See §8.3 |

### 8.2 Capture state machine

```text
Disabled ──Chat:Enabled=false──────────────────────────────► Disabled
Idle ──session Recording + ActiveJob != null───────────────► Connecting
Connecting ──socket open, CAP ACK, JOIN sent───────────────► Joining
Joining ──ROOMSTATE / first PRIVMSG────────────────────────► Live
Live ──PING─────────────────────────────────────────────────► Live (PONG)
Live ──RECONNECT / close / error───────────────────────────► Backoff
Backoff ──delay─────────────────────────────────────────────► Connecting
Live/Joining ──NOTICE msg_channel_suspended / banned───────► Unavailable
Live/Backoff ──session Complete / channel removed──────────► Closing
Closing ──flush, meta complete, close socket───────────────► Idle
```

- One multiplexed connection for all active sessions; `JOIN`/`PART` per channel with a 600 ms queue (under the 20 joins/10 s limit).
- On reconnect, rejoin every active channel; write a `gap` event per channel from `lastMessageAt` to rejoin.
- `Chat:TwitchUserOAuthToken` set → `PASS oauth:<token>` + `NICK <Chat:TwitchLogin>`; validate at startup via `/oauth2/validate` and fall back to anonymous on failure. Never log the token.
- Anonymous handshake: `CAP REQ :twitch.tv/tags twitch.tv/commands`, `NICK justinfan<random>`, `JOIN #channel`; use a dummy PASS only if the server rejects the no-PASS handshake. This is the undocumented part; the prototype decides.
- Any failure sets `meta.status="unavailable"` only when no messages were captured; otherwise keep `recording`/`complete` and add `lastError`. The recording path is never affected.

### 8.3 Test matrix

**Unit (xUnit, `tests/StreamKeepr.Tests`)**

- Parser: tags with `\s`, `\:`, `\\`, `\r`, `\n`; multiple messages per frame; partial frame; PING/PONG; PRIVMSG with all tags; ACTION; emoji + emote ranges (code points); CLEARMSG (`target-msg-id`); CLEARCHAT user/all; NOTICE; ROOMSTATE; RECONNECT; malformed/huge lines; unknown commands ignored.
- Timeline: first PDT, per-part duration, media start, lead-in, gap, overlap, missing PDT, reopen, DST/timezone, offset clamp.
- Store: bucket rotation, late message to previous bucket, partial trailing line, malformed line, dedupe, clear/delete events, `meta` atomic, disk cap, read window, limit, one-writer/many-readers.
- Endpoint: 200/400/404, `no-store`, public, `syncOffsetSec` clamp, `state` values, `hasChat` false.

**Component (bUnit, `tests/StreamKeepr.Tests`)**

- `ChatPane` renders the skeleton once, calls `init` with the recording id, never re-renders on message updates, toggles `ChatOpen`, persists `sk-ui`, hides when `HasChat` is false, and disposes.
- `UiPreferences` parses/writes the `"chat"` field, defaults false, ignores unknown fields, and keeps the 4 KB cap.
- `Home` renders the player row with the pane only when `HasChat`; the player column order remains player → details → stats.

**JS (Vitest, `tests/js`)**

- Fetch window, 5 s overlap, dedupe, pending queue reveal by `currentTime`, pause/seek/speed, backward/forward jump resets, auto-scroll pin/unpin, new-message count, jump button, nudge persistence/clamp, `sk:playhead` filtering by recording id, `textContent`/no `innerHTML`, emote `<img>` alt/srcset, color contrast fallback, reduced motion, keyboard `C`.
- Player test: `sk:playhead` throttle and immediate events; global key handler ignores `[data-sk-chat]`.

**E2E (Playwright, `tests/StreamKeepr.E2E`)**

- Finished Mock recording: open chat, play/pause/seek, messages appear at the expected media times, nudge shifts them, toggle persists across reload, keyboard `C`.
- Growing Mock capture: chat follows the live edge, new messages appear, seek back refetches, finalize switches to complete without a reload.
- Narrow viewport: pane stacks below the player at <960 and is collapsed by default; theater keeps it; fullscreen excludes it.
- axe clean and keyboard-only; no console errors; chat unavailable state (fake failure) does not affect playback.

## 9. Edge cases and failure modes

| Scenario | Behavior |
|---|---|
| Chat starts after the recorder | First ~12 s of recorded media has no chat (lead-in is before capture). Show a gap at the start; optional pre-roll ring buffer later. |
| IRC unavailable / anonymous rejected | `meta.status="unavailable"`, pane shows “Chat unavailable”; recording unaffected. Token fallback if configured. |
| IRC reconnect | Gap event; no replay; rejoin; duplicate IDs deduped. |
| Recorder reconnect / part gap | Messages with no part window are skipped; `gaps` returned; media timeline stays contiguous. |
| Part overlap (restart rewinds to live edge) | Assign to earliest part; dedupe by `id`; never show twice. |
| App restart mid-session | Chat file append continues; meta may be stale; session manifest decides live/complete; duplicates deduped. |
| Session finalized | `meta.status="complete"`, `endedAt`, flush, close; pane switches to finished mode. |
| Recording deleted | Session folder (including `chat/`) removed; endpoint 404; pane stops. |
| CLEARMSG / CLEARCHAT | Events stored; query filters deleted text; UI removes nodes. Text remains on disk until a compaction pass (deferred). |
| Out-of-order/duplicate IRC | 1–2 s reorder buffer; dedupe by `id`; query sorts by `(mt,id)`. |
| Emoji / supplementary characters | Emote ranges are code points; JS slices by code points, not UTF-16 units. |
| XSS attempt | `textContent`; validated colors/IDs; no `innerHTML`; links not clickable. |
| Disk full / chat cap | Stop capture, mark truncated/gap, log once; recording continues or stops via its own disk guard. |
| Very large chat | 10-min buckets + limit 300 + rolling 200–300 rows; no full-session DOM. |
| Multi-tab | Each tab polls; nudge is per browser; last write wins; no server state. |
| Fullscreen/theater | Fullscreen excludes chat; theater keeps it; pane state persists. |
| Token invalid | `/oauth2/validate` fails; log login/reason only; fall back to anonymous; never log the token. |

## 10. Risks and unknowns

| # | Risk / unknown | Impact | Resolution |
|---|---|---|---|
| U1 | Anonymous IRC still works in Nov 2026 and under what limits | Capture may fail silently | 1 h setup, then run unattended for 24 h: `justinfan`, one channel, observe PING/RECONNECT/CLEARMSG; no credentials |
| U2 | Exact `leadInSec` / Twitch segment duration | ±2–5 s sync error | Measure source playlist `EXT-X-TWITCH-ELAPSED-SECS` vs first part PDT once; store per session; nudge |
| U3 | Developer Agreement storage/retention vs public replay | Policy/legal risk | Owner decision; retention setting; honor deletions; consider access control |
| U4 | EventSub user-token arbitrary-channel boundary | Fallback viability | Test with a dedicated user token; docs say `user:read:chat`; practical authorization may require broadcaster/mod |
| U5 | Emote ranges are code points (docs omit unit) | Mis-rendered emotes with emoji | Test `👩‍❤️‍💋‍👩 Kappa`; use code-point slicing |
| U6 | Anonymous IRC can read sub-only/followers-only/verified-only | Missing chat in restricted rooms | Manual test; token fallback if needed |
| U7 | `CLEARMSG`/`CLEARCHAT` delivery to anonymous IRC | Deleted text remains visible | Test with a moderated channel; events are best-effort; compaction later |
| U8 | GQL `contentOffsetSeconds` stability | Calibration may break | Optional only; fail silently; never primary |
| U9 | Chat volume/10-min bucket performance | Slow queries/disk | Load test 50 msg/s for 1 h; limit + buckets; p95 < 50 ms |
| U10 | `HasChat`/size changes affect library tests | Contract churn | Add `HasChat` at the end of the record; keep `SizeBytes` media-only; update tests |

**Top three for the owner:** U1 (anonymous IRC durability), U2 (sync accuracy), U3 (storage/ToS).

## 11. Contract amendments and open questions

### 11.1 Contract amendments

| Contract | Change |
|---|---|
| `docs/session-store-contract.md` | Add `chat/meta.json`, `chat/events.jsonl`, `chat/*.jsonl`; `HasChat` derived; `SizeBytes` excludes chat; finalize/delete/recovery behavior; `ChatTimeline` reads part PDT under the session lock |
| `docs/player-contract.md` | Add the `sk:playhead` DOM event (250 ms throttle + immediate state events); player global keys ignore `[data-sk-chat]`; chat is outside fullscreen |
| `docs/frontend-foundation-contract.md` | `.sk-player-row` sidecar layout; `sk-ui` `"chat"` field/default/cap; `sk-chat` sync-offset schema; 1280 px recording main on wide screens; stack < 960 |
| `docs/backend-ui-contract.md` | `RecordingView.HasChat`; `GET /api/recordings/{id}/chat` response shape; public read |
| `docs/hosting-contract.md` | Endpoint map row; `no-store`; chat files never served; no media allowlist change |
| `docs/twitch-auth-contract.md` | Optional `Chat:TwitchUserOAuthToken` with `chat:read` (not the admin OAuth scope); validation/fallback; secret handling |
| `docs/testing-contract.md` | Parser/timeline/store/endpoint/bUnit/Vitest/E2E layers for chat |
| `docs/recorder-contract.md` | **No change** in the recommended design; the recorder still writes only part files |
| `docs/adr/` | New ADR for the anonymous-IRC dependency and the retention/ToS decision, if the owner proceeds |

### 11.2 Open questions

1. **Anonymous IRC or dedicated chat token?** Is the undocumented anonymous path acceptable for the owner's instance, or should v1 require `Chat:TwitchUserOAuthToken` with `chat:read`? The token fallback is config-only, not admin sign-in.
2. **Retention and public exposure.** Keep chat as long as the recording, cap it (e.g., 30/90 days), or exclude chat from public replay? This is the Developer Agreement/privacy decision.
3. **Pane default.** Collapsed by default, or open on wide screens? 320 px or 340 px? The recommendation is collapsed, 320 px.
4. **Scope of v1 rendering.** Confirm deferring badge images, `USERNOTICE`, third-party emotes, and clickable links. The recommendation is yes.
5. **VOD calibration.** Is the unofficial GQL calibration acceptable as an optional post-finalize pass? The recommendation is defer until the owner approves.
6. **Export.** Should chat be exportable (JSONL/sidecar) with the MP4, or is folder backup enough? The recommendation is folder backup only in v1.
7. **`Chat:Enabled` default.** Ship off and enable on the owner's instance, or default on? The recommendation is off until the prototype passes.

## 12. Sources

All fetched 2026-11-13 unless noted.

**Twitch official**

- IRC concepts: https://dev.twitch.tv/docs/chat/irc/
- Chat & chatbots overview / rate limits: https://dev.twitch.tv/docs/chat/
- Chat authentication: https://dev.twitch.tv/docs/chat/authenticating/
- Migrating from IRC: https://dev.twitch.tv/docs/chat/irc-migration/
- EventSub chat reference: https://dev.twitch.tv/docs/eventsub/reference/chat/
- EventSub managing subscriptions: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/
- Chat API reference (badges/emotes/template): https://dev.twitch.tv/docs/api/reference/chat/
- Videos API reference: https://dev.twitch.tv/docs/api/reference/videos/
- Developer Agreement (last modified 2024-12-04): https://www.twitch.tv/p/en/legal/developer-agreement/
- Docs changelog: https://dev.twitch.tv/docs/change-log/
- 2024 concurrent join limits announcement: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997
- 2025 justinfan reply: https://discuss.dev.twitch.com/t/can-i-use-wss-irc-ws-chat-twitch-tv-with-justifan/63732
- 2024 "never officially documented" reply: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997/17

**FFmpeg**

- Formats docs, HLS `program_date_time`: https://ffmpeg.org/ffmpeg-formats.html#hls-2
- `libavformat/hlsenc.c` (master), `initial_program_date_time = av_gettime() / 1000000.0`: https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/hlsenc.c#L2908
- `libavformat/hlsplaylist.c` (master), PDT write/increment: https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/hlsplaylist.c#L189-L190

**Streamlink**

- CLI `--hls-live-edge`: https://streamlink.github.io/cli.html#cmdoption-hls-live-edge
- Twitch plugin docs (2 s segments, fMP4 PTS offset): https://streamlink.github.io/cli/plugins/twitch.html
- `src/streamlink/stream/hls/hls.py` at 8.6.3, `edge_index`: https://github.com/streamlink/streamlink/blob/8.6.3/src/streamlink/stream/hls/hls.py#L507-L508
- Same file, `_write_plain`/`iter_segment_content`: https://github.com/streamlink/streamlink/blob/8.6.3/src/streamlink/stream/hls/hls.py#L339-L374

**IRC / libraries / evidence**

- IRCv3 message tags and escaping: https://ircv3.net/specs/extensions/message-tags
- Emote code-point evidence: https://discuss.dev.twitch.com/t/cant-calculate-offset-from-the-emotes-tag-if-the-message-contains-emojis/28414
- TwitchLib.Client NuGet: https://www.nuget.org/packages/TwitchLib.Client
- TwitchLib repository: https://github.com/TwitchLib/TwitchLib
- TwitchLib anonymous example: https://github.com/TwitchLib/TwitchLib/issues/1086
- TwitchDownloader GQL source: https://github.com/lay295/TwitchDownloader/blob/master/TwitchDownloaderCore/ChatDownloader.cs
- chat-downloader GQL source: https://github.com/xenova/chat-downloader/blob/master/chat_downloader/sites/twitch.py
- Emote CDN handling: https://dev.twitch.tv/docs/chat/send-receive-messages/#handling-emotes

**Repo citations** (at `d47e4a0`)

- `src/StreamKeepr/Configuration/RecorderOptions.cs:20,22` — 6 s segments, live edge 6.
- `src/StreamKeepr/Infrastructure/Media/FfmpegArgs.cs:29` — `+program_date_time`.
- `src/StreamKeepr/Infrastructure/Storage/PlaylistStitcher.cs:80-90,198-249` — PDT preserved/parsed.
- `src/StreamKeepr/Infrastructure/Storage/SessionStore.cs:76-83,149-201,263-340` — manifest `StartedAt`, live stitch, finalize.
- `src/StreamKeepr/Domain/Session.cs:24,31` — `StartedAt` from `LiveStream`.
- `src/StreamKeepr/Infrastructure/Twitch/TwitchModels.cs:18` — Helix `started_at`.
- `src/StreamKeepr/Infrastructure/Runtime/DvrStatus.cs:141` — `JobStartedAt`.
- `src/StreamKeepr/Infrastructure/Storage/RecordingLibrary.cs:91-152` — startup scan/`Changed`.
- `src/StreamKeepr/Components/Pages/Home.razor:37-56` — player/details/stats slots.
- `src/StreamKeepr/Components/Features/Player/VideoPlayer.razor:13-24` — static skeleton; `:74-80` control order.
- `src/StreamKeepr/Components/Features/Player/VideoPlayer.razor.js:691-699,1043-1045` — timeupdate/seek.
- `src/StreamKeepr/wwwroot/css/components.css:227,307-308,409` — 16:9 player, container queries, 1100 px main.
- `docs/session-store-contract.md:9,24-35,53-59` — folder ownership, manifest, atomic writes.
- `docs/recorder-contract.md:33-52` — FFmpeg args.
- `docs/player-contract.md:17-19,54-57,65-80,107-111` — JS ownership, live edge, control order, keys.
- `docs/frontend-foundation-contract.md:38-52,71-76,142` — shell, cookie, JS cadence rule.
- `docs/backend-ui-contract.md:23-28,193` — `RecordingView`, delete.
- `docs/hosting-contract.md:35-48,55-66` — endpoint map, media allowlist.
- `docs/testing-contract.md:13-20` — test layers.

[irc]: https://dev.twitch.tv/docs/chat/irc/
[chat-limits]: https://dev.twitch.tv/docs/chat/
[chat-auth]: https://dev.twitch.tv/docs/chat/authenticating/
[irc-migration]: https://dev.twitch.tv/docs/chat/irc-migration/
[es-chat]: https://dev.twitch.tv/docs/eventsub/reference/chat/
[es-manage]: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/
[api-chat]: https://dev.twitch.tv/docs/api/reference/chat/
[api-videos]: https://dev.twitch.tv/docs/api/reference/videos/
[dev-agreement]: https://www.twitch.tv/p/en/legal/developer-agreement/
[changelog]: https://dev.twitch.tv/docs/change-log/
[join-limits]: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997
[justinfan-2025]: https://discuss.dev.twitch.com/t/can-i-use-wss-irc-ws-chat-twitch-tv-with-justifan/63732
[justinfan-undoc]: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997/17
[ircv3-tags]: https://ircv3.net/specs/extensions/message-tags
[emote-codepoints]: https://discuss.dev.twitch.com/t/cant-calculate-offset-from-the-emotes-tag-if-the-message-contains-emojis/28414
[emote-cdn]: https://dev.twitch.tv/docs/chat/send-receive-messages/#handling-emotes
[sl-cli]: https://streamlink.github.io/cli.html#cmdoption-hls-live-edge
[sl-twitch]: https://streamlink.github.io/cli/plugins/twitch.html
[sl-hls]: https://github.com/streamlink/streamlink/blob/8.6.3/src/streamlink/stream/hls/hls.py#L507-L508
[ff-formats]: https://ffmpeg.org/ffmpeg-formats.html#hls-2
[hlsenc]: https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/hlsenc.c#L2908
[hlsplaylist]: https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/hlsplaylist.c#L189-L190
[td-gql]: https://github.com/lay295/TwitchDownloader/blob/master/TwitchDownloaderCore/ChatDownloader.cs
[cd-gql]: https://github.com/xenova/chat-downloader/blob/master/chat_downloader/sites/twitch.py
