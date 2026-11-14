# Chat capture depends on chat storage, never the reverse

## Status

Accepted (2026-11-14). Follows PR #156; tracked in issue #153.

## Context

Chat capture spans two folders. `Infrastructure/Twitch/Chat` holds the sources (`IrcChatClient`, and `Mock/MockChatSource` in the Mock profile), the IRC parser, the bounded `ChatEventQueue`, and `ChatCaptureService`, which routes source events to the log. `Infrastructure/Storage` holds the chat log (`ChatLogStore`, `ChatSessionWriter`, `ChatReader`, `ChatTombstones`, `ChatLogPurge`).

PR #156 left the two folders importing each other: Storage imported `ChatSourceEvent` and `ChatMessage` from Twitch/Chat, and the sources read their `source` names (`irc-anonymous`, `irc-token`, `mock`) from `ChatLogStore`. Neither side could change without considering the other.

`ChatCaptureService` necessarily uses both the sources and Storage (`RecordingLibrary`, `ChatLogStore`), so Storage cannot be the side that depends on Twitch/Chat without pulling the capture loop into Storage.

## Decision

- `Infrastructure/Twitch/Chat` (and the Mock chat source) MAY depend on `Infrastructure/Storage`. `Infrastructure/Storage` MUST NOT import `StreamKeepr.Infrastructure.Twitch.Chat`.
- The records that cross the seam are the chat log's input contract and live in Storage: `ChatSourceEvent` and its cases, `ChatMessage`, `ChatBadge`, `ChatEmote`, `ChatEmoteRange`, and `ChatReply` (`Storage/ChatSourceEvents.cs`). Sources produce them; `ChatSessionWriter` maps them to the stored JSONL rows.
- Transport-only types stay in Twitch/Chat: `IChatSource`, `ChatEventQueue`, `ChatConnectionState`, the IRC parser and socket types.
- The `source` vocabulary written to `meta.json` and returned by the chat query lives in `Storage.ChatSources` beside `ChatStates`. Each source reports one of those names; storage records it as given.

## Consequences

- A new transport (EventSub or TCP/TLS IRC) only has to emit `ChatSourceEvent`s and report a `ChatSources` name; storage needs no change.
- Storage can be built and tested with no IRC types in scope.
- Adding a field to `ChatMessage` is a Storage change, because the writer maps every field to disk. That matches who owns the on-disk format.
- The rule is not enforced by a separate assembly. A `using StreamKeepr.Infrastructure.Twitch.Chat;` in `Infrastructure/Storage` is a review failure.
