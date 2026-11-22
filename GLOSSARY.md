# Stream Keepr

Stream Keepr records Twitch streams to disk as HLS and plays them back in a Blazor web app. This glossary fixes the vocabulary used by the code, issues, and specification.

## Language

**Stream**:
A live Twitch broadcast (`Broadcast type` = `live`), identified by its Twitch stream ID.
_Avoid_: Video, feed, non-live broadcast

**Broadcast type**:
Twitch's classification of an online broadcast: `live`, `playlist`, `watch_party`, `premiere`, or `rerun`.
_Avoid_: Stream type, event type (use “event.type” only for the wire field)

**Non-live broadcast**:
An online broadcast whose broadcast type is not `live`; Stream Keepr observes it as Offline for recording.
_Avoid_: Rerun (unless that specific type is meant), stream

**Channel**:
A watched Twitch login that Stream Keepr records.
_Avoid_: User, account, streamer (use “streamer” only for the person in UI copy)

**Resolved channel**:
A watched channel whose Twitch user ID is known; EventSub can subscribe to it.
_Avoid_: Eligible channel, linked channel

**Unresolved channel**:
A watched channel whose Twitch user ID is not known; it stays on polling until the profile is resolved.
_Avoid_: Unlinked channel, profile-less channel

**Channel colour**:
The colour that identifies a channel's Twitch login wherever it appears, such as its avatar and its name in chat. It belongs to the login, not to the watch, so recordings of a removed channel keep it.
_Avoid_: Streamer colour, user colour

**Session**:
One continuous recording of a stream, identified by the stream's start time and stream ID. A session owns one or more parts and a manifest.
_Avoid_: Recording (use “recording” for the product concept, not the domain object), capture

**Open session**:
A session that is still being written: its channel is Recording, or the session is reconnecting after its recorder stopped.
_Avoid_: Active session, live session, in-progress session

**Complete session**:
A session that is no longer being written, whether it was saved in one part or in several.
_Avoid_: Finished session, finalized session, done session

**Part**:
One recorder run's media within a session, stored as `pNNN` files.
_Avoid_: Segment, chunk, file

**Part playlist**:
The HLS playlist FFmpeg writes for one part (`pNNN.m3u8`). It counts as having segments only when it holds at least one complete segment line.
_Avoid_: Part index, sub-playlist

**Session index**:
The one playlist for a whole Complete session (`index.m3u8`): every part playlist that has segments, stitched in part order and closed with `#EXT-X-ENDLIST`.
_Avoid_: Final playlist, master playlist

**Live index**:
The stitched playlist served while a session is Recording. It is built by the same rules as the session index but never ends with `#EXT-X-ENDLIST`.
_Avoid_: Live playlist, live stitch

**Job**:
One recorder execution for one part, identified by `JobId` (session + part number).
_Avoid_: Task, process (unless referring to the OS process)

**Resumable**:
A session plus the next part number that can be resumed or reopened.
_Avoid_: Pending session, unfinished

**Healthy part**:
A media-producing part that ran at least `HealthyPartSeconds` and exited on its own.
_Avoid_: Good part, successful part

**Streak**:
The count of consecutive short or failed parts since the last healthy part. It drives cooldown.
_Avoid_: Retry count, failure count

**Recording**:
The state in which a recorder job is running for a channel.
_Avoid_: Capturing, live (live describes the stream, not the recorder)

**Idle**:
The state in which no recorder is running; it may hold a resumable or complete session.
_Avoid_: Stopped, inactive

**Paused**:
The state in which recording is stopped and live observations are ignored until the user resumes.
_Avoid_: Stopped, disabled

**CoolingDown**:
The state in which repeated short or failed parts suppress recording until a time.
_Avoid_: Backoff, cooldown (use “backoff” for the delay between retries)

**Unknown**:
A Twitch observation where the API failed. Unknown is never treated as Offline.
_Avoid_: Error, offline

**Recorder exit kind**:
Why a recorder process ended: `Exited` (on its own), `Stopped` (coordinator requested), or `Killed` (watchdog or force kill).
_Avoid_: Exit code, status

**Part outcome**:
The result of one Job, built on every path: exit kind, the part's size on disk (or unknown), duration, diagnostic detail, and an unverified shutdown if one occurred. The Recorder exit kind and all recorder metrics derive from it.
_Avoid_: Job result, exit status

**Unverified shutdown**:
A Job end where Stream Keepr cannot prove the recorder processes stopped writing. It is fatal: the channel keeps the Job and the host stops, so no second writer starts.
_Avoid_: Hung recorder, zombie, failed stop

**Session recovery**:
The startup reconstruction of the newest session for a channel as resumable or complete.
_Avoid_: Rebuild, restore

**Chat capture**:
The best-effort process of reading Twitch chat for a session and persisting it as the session's chat log. Chat capture is independent of the recording state and never gates recording.
_Avoid_: Chat recording, chat history capture

**Chat log**:
The persisted per-session record of chat messages and chat log events. It is complete only while capture was connected; gaps are explicit.
_Avoid_: Chat history, transcript

**Chat message**:
One user-authored Twitch chat message (`PRIVMSG`) for a channel, identified by its Twitch message ID and timestamped with `tmi-sent-ts` when available.
_Avoid_: Chat line, comment

**Emote**:
A chat token that renders as an inline image, identified by its text code.
_Avoid_: Emoji, reaction, custom emote

**Twitch emote**:
An emote Twitch identifies in a chat message's IRC `emotes` tag.
_Avoid_: Native emote, built-in emote

**Third-party emote**:
An emote resolved by matching a chat message's text against an emote provider's catalog.
_Avoid_: Custom emote, BTTV emote (unless the provider matters)

**Emote provider**:
A service that publishes emote catalogs for Twitch channels, such as BetterTTV or 7TV.
_Avoid_: Emote source, emote service

**Emote catalog**:
A provider's global and channel-specific set of emote codes and image references.
_Avoid_: Emote list, emote set

**Chat log event**:
A non-message record in the chat log: deletion, clear, room-state change, notice, gap, dropped events, truncation, end, or purge.
_Avoid_: System message, moderation event (when the general record is meant)

**Chat replay**:
The pane that displays the chat log synchronized to recording playback.
_Avoid_: Chat history pane, live chat

**Chat gap**:
An interval in which chat capture was not connected or could not persist events: an IRC outage until rejoin, events dropped by a full capture queue, a truncated log, or unrecorded time between parts. A gap is shown, never filled by guessing.
_Avoid_: Missing messages, lost chat

**Chat sync offset**:
A per-recording viewing adjustment that shifts chat replay earlier or later relative to the recording.
_Avoid_: Chat delay, latency

**Live edge**:
The newest playback position the player treats as live: the sync position about one target duration behind the end of a session that is still Recording or Reconnecting.
_Avoid_: Live point, now, end of stream

**Following**:
The player mode in which playback stays at the live edge while the session is still being recorded. A pause or a seek behind the live edge leaves following; returning to the live edge by any means resumes it.
_Avoid_: Live mode, auto-follow, catching up

**Resume position**:
The one saved playback position per recording, shared by every viewer of the app, where playback starts when the recording is opened. It exists only while the viewer is away from the live edge and short of the end: following, reaching the end, or a capture finishing within 30 s of it discards it.
_Avoid_: Watch progress, bookmark, last position
