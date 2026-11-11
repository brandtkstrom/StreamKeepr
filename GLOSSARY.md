# Stream Keepr

Stream Keepr records Twitch streams to disk as HLS and plays them back in a Blazor web app. This glossary fixes the vocabulary used by the code, issues, and specification.

## Language

**Stream**:
A live Twitch broadcast, identified by its Twitch stream ID.
_Avoid_: Video, broadcast, feed

**Channel**:
A watched Twitch login that Stream Keepr records.
_Avoid_: User, account, streamer (use “streamer” only for the person in UI copy)

**Session**:
One continuous recording of a stream, identified by the stream's start time and stream ID. A session owns one or more parts and a manifest.
_Avoid_: Recording (use “recording” for the product concept, not the domain object), capture

**Part**:
One recorder run's media within a session, stored as `pNNN` files.
_Avoid_: Segment, chunk, file

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
The state in which no recorder is running; it may hold a resumable or finished session.
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

**Session recovery**:
The startup reconstruction of the newest session for a channel as resumable or finished.
_Avoid_: Rebuild, restore
