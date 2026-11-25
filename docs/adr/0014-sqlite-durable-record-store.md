# SQLite is the durable record store

## Status

Accepted (2026-11-24). Supersedes ADR-0009's "Do not add a database" (its retention, tombstone, purge and cap rules stand, now applied to database rows) and the implementation plan's "filesystem is the source of truth" (Phase 5). Implemented by #315–#324.

## Context

Durable state is spread across JSON sidecars under the recordings volume: `channels.json`, `progress.json`, `admin-tokens.json`, `eventsub-conduit.json`, per-session `session.json`, and chat JSONL buckets. Each file carries its own atomic-write, corruption and recovery rules, the library is rebuilt by scanning disk, and app data shares a volume with media.

## Decision

- One SQLite database is the application's durable record store for channels, sessions, metadata changes, chat, viewer samples, resume positions, the admin token, and EventSub conduit state. Media (parts, playlists, thumbnails) stays on the filesystem.
- Access goes through EF Core (`Microsoft.EntityFrameworkCore.Sqlite`; `Microsoft.EntityFrameworkCore.Design` at build time only). No other data-access library.
- `Storage:DatabasePath` (default `/data/streamkeepr.db`) is independent of `Storage:RecordingsRoot`; the container has separate `/data` and `/recordings` volumes.
- The database is never reset automatically. A database that cannot be opened, fails a startup integrity check, or carries migrations this build does not know stops startup with a clear error.
- Before pending migrations apply to an existing database, a consistent `VACUUM INTO` backup is written to `backups/` beside the database, keeping the three most recent. A fresh database gets no backup. A failed backup or migration stops startup.
- Channels seed from `Twitch:Channels` once, when the database is created; an emptied channel list stays empty.
- "Fails a startup integrity check" means `PRAGMA quick_check` does not return `ok`; the full `integrity_check` is too slow once chat grows the database.
- The Mock profile uses a per-process database under the system temp directory, deleted on clean shutdown. It is the only database the app ever deletes.
- **Write-failure policy.** Session open fails: the recorder does not start and the job goes to CoolingDown and retries. Finalize fails: the session stays Open and startup session recovery finalizes it. A metadata change fails: log and keep recording. A chat write fails: log and skip (as ADR-0009).
- **Clean break.** Legacy sidecars are never read or imported. Upgrading resets the channel list to the config seed, hides pre-upgrade recordings from the library (their media is untouched), and signs the admin out. Startup logs once when legacy files are present and ignored.

## Consequences

- An upgraded install shows an empty library until new recordings are made; old media must be managed by hand.
- Backups of the deployment now need both volumes.
- The schema starts as one initial migration owned by #315; later tickets coordinate before adding migrations.
