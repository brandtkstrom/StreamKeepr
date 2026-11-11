# LibraryDrawer

The left pane listing recordings grouped by streamer, newest first at every level.

**Ordering rules (from the brief):**
1. Streamer groups are ordered by their most recent recording, descending — a streamer that is recording right now is always first.
2. Inside a group, recordings are grouped under day headers (`Today`, `Yesterday`, then `Wed, Nov 4`) and ordered by start time, descending.
3. The header shows the sort as "Newest first"; the options menu may offer "Oldest first" and "A–Z by streamer", but newest-first is the default and is restored on reload.

**Parts.** `.sk-group` (collapsible, `aria-expanded`) → `.sk-group-head` (avatar, name, count · last capture, chevron) → `.sk-day` + `.sk-row`s. A row is thumbnail (`thumb-width`, 16:9, duration badge in `timecode`, resume-progress bar in `primary`), title (`title-small`, two lines max), meta (`body-small`) and a status chip only when the state is not plain "saved".

**States.** Selected row: `aria-current="true"` → `secondary-container` ground with a 2px `primary` ring on the thumbnail. Recording in progress: red ring on the avatar, REC chip, `rec` progress bar.

**Collapse.** Each streamer group collapses independently; the whole drawer collapses to a 72px rail of avatars (`.rail`) and hides fully in theater mode (`.hidden`).

**Consumer provides:** `[{streamer, avatarUrl?, isRecording, recordings:[{id, title, startedAt, durationSec, state: "rec"|"processing"|"partial"|"saved", resumeSec?, thumbnailUrl?}]}]`, already sorted.
