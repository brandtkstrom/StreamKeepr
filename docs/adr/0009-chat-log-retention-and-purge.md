# Chat log retention and opt-in purge

## Status

Accepted (2026-11-13). Scoped to the private-home-use project posture.

## Context

Chat replay needs a persisted per-session record with media-time lookup. The repository has no database and already uses plain files with atomic temp+rename writes. Chat contains user IDs, display names, message text, and quoted replies. Twitch's Developer Agreement restricts storing Twitch Content beyond a short cache; the project's private-home-use statement is the exposure boundary, not a legal exception.

## Decision

- Store chat in `recordings/{channel}/{session}/chat/` as 10-minute UTC JSONL buckets plus `events.jsonl` and `meta.json`. Do not add a database.
- Keep chat for the life of the recording; deleting the recording deletes `chat/`. No retention service.
- Store `CLEARMSG`/`CLEARCHAT` as tombstones and hide deleted text at query time, including reply quotes of removed messages. Query time and purge share one tombstone rule (`ChatTombstones`). The raw text remains until an opt-in purge.
- `Chat:PurgeDeletedText` is `Off` by default or `OnFinalize`.
- An `OnFinalize` purge rewrites affected buckets atomically: remove deleted message rows, redact surviving replies that quote removed messages, remove the delete/clear events, and keep non-content `gap`/`dropped`/`truncated`/`end` markers plus a `purged` count. A reply is redacted when its parent was removed in any bucket, or when its parent's author was cleared at or after the reply (the parent may predate capture).
- Cap each session with `Chat:MaxBytesPerSession` (default 512 MiB). A full chat log is truncated and marked; a failing chat write is logged and skipped. Neither blocks recording.
- Do not retroactively purge already-finalized recordings in v1.

## Consequences

- Deleted text remains on disk during a live session and until finalize when purge is enabled. The query-time tombstones still hide it from the pane.
- Purge can fail partway. Each bucket rewrite is atomic, but earlier buckets may already be purged; the failure is logged and the query path continues to hide deleted text from the rest.
- Chat adds personal data to the recordings volume. The operator is responsible for access, backups, and deletion in the private-home deployment.
- A future compaction or retention policy is a new decision; this ADR deliberately ships only finalize-time purge.
