# Research memo: IRC for Twitch chat capture

Status: research memo, 2026-11-13.
Issue: no dedicated IRC issue. `gh issue list --search irc --state all` returns no results; the nearest item is #131 (EventSub conduits). This memo builds on `docs/research/twitch-chat-replay.md`; it does not repeat that memo's replay, timing, persistence-schema, or sidecar-pane design.
Binding: no. This memo informs a decision; it changes no contract. Adoption would amend `docs/session-store-contract.md`, `docs/runtime-contract.md`, `docs/hosting-contract.md`, `docs/backend-ui-contract.md`, `docs/twitch-auth-contract.md`, `docs/testing-contract.md`, and the decision ledger in `docs/decision-digest.md`, and would add an ADR for the anonymous-IRC dependency and the retention decision.

**Method.** IRC facts come from RFC 1459, RFC 2810-2813, RFC 7194, the IRCv3 specifications, and `modern.ircdocs.horse`. Twitch facts come from the official Twitch IRC, chat, authentication, migration, and changelog pages plus Twitch Developer Forums announcements and staff replies. .NET facts come from Microsoft Learn. Implementation evidence comes from Chatterino's current source and Streamlink's HLS source. All web sources were fetched on **2026-11-13**; links are in §9. No Twitch API was called, no credentials were used, the app was not run, and no external IRC network was contacted. Repository citations are `path:line` from the working tree on 2026-11-13 (HEAD `2a8b2df`). Serena's C# language server again returned only the namespace for `EventSubClient.cs`, so the code was read with targeted `read` calls (reported per `AGENTS.md`).

## 1. Summary and recommendation

**Recommendation: GO with IRC over WebSocket (`wss://irc-ws.chat.twitch.tv:443`) as StreamKeepr's read-only chat capture transport, behind `Chat:Enabled=false` and conditional on a timeboxed anonymous-IRC prototype (1 h setup + 24 h soak) and one owner retention decision.** Use an anonymous `justinfan<digits>` identity by default, with an optional dedicated `Chat:TwitchUserOAuthToken` holding `chat:read` as the documented fallback. Persist chat in the session's `chat/` folder using the JSONL layout already proposed in `docs/research/twitch-chat-replay.md` §5. Do **not** use IRC for history, replay, or guaranteed delivery; do not add a database; do not use EventSub chat as the primary path; keep polling as the recording safety net.

The reasoning is narrow. IRC is the only chat path that works for arbitrary public channels without per-broadcaster authorization and without coupling capture to the admin's browser session. EventSub chat is cleaner and better documented, but its practical access requires the authorized user to be the broadcaster or a moderator, or the broadcaster to grant `channel:bot`; StreamKeepr cannot obtain that for arbitrary channels. The existing memo reached the same conclusion (`docs/research/twitch-chat-replay.md:81-94`); this memo validates the protocol details and makes the transport/parser/state-machine plan concrete.

Conditions:

1. **Prototype anonymous IRC for ≤ 1 hour, then soak it for 24 hours.** Current official docs require `PASS oauth:<user-token>` with `chat:read` and no longer document `justinfan`. Anonymous read-only access is still used by Chatterino and was described as "should still work" by a Twitch staff reply in 2025, but it is undocumented and its limits are **UNKNOWN**. If it fails, the fallback is a dedicated chat user token, not an admin-scope change.
2. **Owner decision: chat storage/retention vs the Twitch Developer Agreement.** The agreement restricts storing Twitch Content beyond a 24-hour cache and requires deletion on request. Keeping chat with recordings for replay is a policy risk that needs an explicit owner decision; this memo does not give legal advice.
3. **Accept gaps and approximate sync.** IRC does not replay messages missed during a disconnect. The pane must show gaps, deduplicate by message ID, and never claim completeness. The timing model remains the prior memo's PDT + `leadInSec` + user nudge (`docs/research/twitch-chat-replay.md:124-195`).

What this memo adds: an IRC primer, protocol-level parsing rules, the WSS-vs-TCP/TLS decision, a connection state machine with timeouts, backpressure and reconnect behavior, a concrete C# file map, and a test plan. It deliberately does not re-litigate the replay UI.

## 2. What IRC is

### 2.1 Origin and standards

IRC (Internet Relay Chat) is an application-layer, text-based, client-server protocol for real-time group chat. Jarkko Oikarinen wrote the first IRC client and server at the University of Oulu in August 1988, initially to add real-time chat to the OuluBox BBS. Oikarinen's own retrospective says "The birthday of IRC was in August 1988" ([Oikarinen history][irc-history]). RFC 1459, published May 1993, corroborates the timing: it says the protocol "was developed over the last 4 years since it was first implemented as a means for users on a BBS to chat amongst themselves" ([RFC 1459][rfc1459], Abstract). The exact August 1988 date rests on Oikarinen's retrospective, not on an RFC.

| Standard | Date | What it fixes |
|---|---|---|
| RFC 1459 | May 1993 | Original IRC protocol: architecture, message grammar, commands, numerics. Experimental status. |
| RFC 2810 | April 2000 | Architecture: client-server model, server networks, channels, message routing. |
| RFC 2811 | April 2000 | Channel management: names, modes, operators, voice, bans, keys, limits. |
| RFC 2812 | April 2000 | Client protocol: registration, commands, numerics, message format. |
| RFC 2813 | April 2000 | Server-to-server protocol. |
| RFC 7194 | August 2014 | Informational: the commonly accepted TLS port is 6697. It updates RFC 1459. |
| IRCv3 | 2010s-present | Opt-in protocol extensions negotiated with `CAP`: message tags, SASL, batch, history, etc. Not a replacement protocol. |

The RFCs describe one IRC network as a set of servers connected in a spanning tree; in practice there are many independent networks, each with its own server set. Twitch is a single closed network that implements a modified subset, not a peer of that federation ([Twitch IRC concepts][twitch-irc]).

### 2.2 Architecture

| Concept | Meaning | Consequence for capture |
|---|---|---|
| Client | A user's connection to one server. | StreamKeepr is a client. It has no server role and opens no inbound port. |
| Server | Relays messages, tracks local users and channels. Servers form a spanning tree and link to each other with the server-to-server protocol ([RFC 2813][rfc2813]). | A message traverses only the servers on the path to channel members. |
| Network | A set of servers sharing state. | Nicknames and channels are scoped to a network; Twitch is one network. |
| Channel | A named group (`#channel`) that exists while it has members. | A channel is not a durable object; it has no message store. |
| Nickname | A user's network-unique name. | Twitch's nickname is the lowercase login. |
| User mask | `nick!user@host`, carried in the message prefix. | Twitch masks look like `ronni!ronni@ronni.tmi.twitch.tv`. |
| Topic | A channel description, usually set by operators. | Twitch topics are channel metadata, not chat messages. |
| Modes | Channel/user flags such as `+o` (operator), `+v` (voice), `+m` (moderated), `+i` (invite-only), `+t` (topic lock), `+k` (key), `+l` (limit). | Twitch does not expose general IRC modes; chat settings arrive as `ROOMSTATE`. |
| Operators/voice | `@` operators can kick, set modes, and change the topic; `+` voice can speak in moderated channels. | Twitch moderators/VIPs are Twitch roles carried in badges, not IRC `MODE` changes. |
| Services | NickServ/ChanServ are network service bots that register nicknames/channels and enforce permissions. They are not part of RFC 1459/2812. | Twitch documents no NickServ/ChanServ; OAuth identifies the user. |
| DCC | Direct Client-to-Client, a CTCP-based file-transfer/direct-chat extension. | Not part of the core protocol; Twitch does not document it; irrelevant here. |
| Bouncer | A persistent proxy (ZNC, soju) that stays connected and replays a buffer to clients. | External history, not protocol history. Twitch does not provide one. |

RFC 2810 defines the client-server model and the spanning-tree server topology ([RFC 2810][rfc2810], §1, §3). RFC 1459 defines channels as named groups that exist only while members are present ([RFC 1459][rfc1459], §1.3). RFC 2811 defines channel modes, operators, and voice ([RFC 2811][rfc2811], §2.4, §4.2). Services such as NickServ are network conventions, not RFC commands; Libera.Chat's registration guide is a representative description ([Libera registration][libera-services]). DCC is a CTCP extension, documented alongside CTCP in the modern IRC client protocol ([modern IRC][modern-irc], "CTCP" and "DCC").

### 2.3 How messages route, and why there is no history

A channel message takes this path:

```text
sender client -> sender's server -> servers on the spanning-tree paths
              -> servers hosting channel members -> recipient clients
```

RFC 2810 §5.1 requires each server to send a message in exactly one direction along the spanning tree to reach a client; §5.2.1 requires channel messages to be sent only to servers supporting users on that channel, and only once per local link ([RFC 2810][rfc2810], §5.1-§5.2). RFC 1459 describes the server's outbound send queue as a FIFO, not a durable log ([RFC 1459][rfc1459], §8.3). The server keeps global state about users and channels, not a transcript of messages. If no client is in a channel, the channel ceases to exist, and there is nothing to replay.

Base IRC therefore has **no history**. A client that connects sees only messages sent after it joins (plus whatever the server chooses to send as join-time state, such as `NAMES` and `TOPIC`). History requires an extension (`chathistory`, still a work-in-progress) or an external bouncer that stores messages ([IRCv3 chathistory][ircv3-chathistory]). Twitch provides neither.

This is the single most important fact for StreamKeepr: IRC is a **live tap**, not an archive. Every disconnect creates an unrecorded interval. The design must write gap markers, not pretend the transcript is complete.

### 2.4 Twitch's IRC is a modified subset, not a general IRC network

Twitch's own description is "a chat interface provided by Twitch based on a modified RFC1459 and IRCv3 Message Tag specification" and "it doesn't support all IRC messages" ([Twitch IRC concepts][twitch-irc]). The practical differences:

| General IRC | Twitch IRC |
|---|---|
| Federated networks; server-to-server links. | One provider; clients connect to `tmi.twitch.tv`; no server-to-server for third parties. |
| `PASS` is an optional connection password; SASL is the modern authentication path. | `PASS oauth:<user-token>` with `chat:read`/`chat:edit`; SASL is not documented. |
| NickServ/ChanServ manage identity and channels. | OAuth and Twitch roles; no NickServ/ChanServ. |
| Channel modes, operators, voice, topics. | `ROOMSTATE` chat settings; moderator/VIP/broadcaster badges; no general `MODE` surface. |
| `PRIVMSG`, `NOTICE`, `JOIN`, `PART`, `QUIT`, `MODE`, `TOPIC`, `NAMES`, `WHO`, `WHOIS`, `KICK`, `INVITE`. | A smaller command set plus Twitch-specific `CLEARCHAT`, `CLEARMSG`, `USERNOTICE`, `ROOMSTATE`, `RECONNECT`, `USERSTATE`, `GLOBALUSERSTATE`. |
| IRCv3 extensions vary by network. | Documented capabilities are `twitch.tv/tags`, `twitch.tv/commands`, `twitch.tv/membership`; it does not document `chathistory`, `sasl`, `batch`, `labeled-response`, or `echo-message`. |
| History via `chathistory` or a bouncer. | No history or replay. |
| DCC, CTCP, multi-network clients. | Not documented/irrelevant. |

Twitch recommends EventSub and the Twitch API over IRC, calling IRC "more complicated to parse" and limited versus EventSub ([Twitch IRC concepts][twitch-irc]). That recommendation is about feature depth, not a removal notice: Twitch still updated IRC PRIVMSG tags in July 2026 ([Twitch changelog][twitch-changelog], 2026-07-17).

## 3. How IRC works at the protocol level

### 3.1 Transport and framing

| Property | Value | Source |
|---|---|---|
| Transport | TCP. The commonly accepted TLS port is 6697 (informational RFC 7194); plaintext is 6667. | [RFC 7194][rfc7194]; [RFC 1459][rfc1459] §8.1 |
| Plaintext | Port 6667. Still listed by Twitch for IRC clients; not recommended. | [Twitch IRC concepts][twitch-irc] |
| Twitch WebSocket | `wss://irc-ws.chat.twitch.tv:443` (TLS) and historically `ws://...:80` (decommissioned 2025-08-15). | [Twitch IRC concepts][twitch-irc]; [decommission announcement][twitch-ws-decommission] |
| Framing | One message per line, terminated by CRLF (`\r\n`). Empty lines are ignored. | [RFC 2812][rfc2812] §2.3; [modern IRC][modern-irc] "Message Format" |
| Legacy line limit | 512 bytes including the trailing CRLF (510 bytes of command and parameters). RFC 1459 says "characters"; modern implementations treat the line as bytes. | [RFC 1459][rfc1459] §2.3; [RFC 2812][rfc2812] §2.3 |
| IRCv3 tag limit | 8191 bytes including the leading `@` and trailing space; the rest of the message remains 512 bytes. Client tag data is capped at 4094 bytes; server-added tag data is capped at 4094 bytes. | [IRCv3 message tags][ircv3-tags] "Size limit" |
| Encoding | IRCv3 tag values MUST be UTF-8. The base protocol predates a charset declaration; Twitch sends UTF-8. | [IRCv3 message tags][ircv3-tags]; [modern IRC][modern-irc] |
| Embedded CR/LF | Not allowed: CR and LF are delimiters; NUL is not special but is disallowed by RFC 2812. | [RFC 2812][rfc2812] §2.3.1 |

**Correction to a common shorthand.** The 2025 decommission applied to **non-secure WebSocket** connections (`ws://irc-ws.chat.twitch.tv:80`), not to plaintext TCP IRC. Twitch's announcement explicitly says the change does not affect "Non-SSL-based IRC (`irc://irc.chat.twitch.tv:6667`)" ([decommission announcement][twitch-ws-decommission]). The current IRC concepts page still lists 6667 ([Twitch IRC concepts][twitch-irc]). This memo nevertheless recommends TLS-only; 6667 is a compatibility fact, not a design choice.

A WebSocket frame may contain one IRC message or several CRLF-delimited messages; the frame boundary is not a message boundary ([Twitch IRC concepts][twitch-irc], "Parsing messages"). A TCP/TLS read may split or coalesce lines arbitrarily. The parser must therefore own line framing in both transports.

### 3.2 Message grammar

The general shape is:

```text
[@tags] [:prefix] command [params] [:trailing]
```

- `tags` is optional IRCv3 metadata, beginning with `@`. Tags are separated by `;`; the first `=` separates key and value.
- `prefix` is optional, begins with `:`, and identifies the source: a server name or `nick[!user][@host]`.
- `command` is a letter-only command or a three-digit numeric.
- `params` are space-separated; up to 15 parameters (14 middle plus a trailing) per RFC 2812.
- `trailing` begins with `:` and may contain spaces; it consumes the rest of the line.

Examples:

```text
:ronni!ronni@ronni.tmi.twitch.tv PRIVMSG #ronni :Kappa Keepo Kappa
@badge-info=;badges=turbo/1;color=#0D4200;display-name=ronni;emotes=25:0-4,12-16/1902:6-10;id=b34ccfc7-4977-403a-8a94-33c6bac34fb8;mod=0;room-id=1337;subscriber=0;tmi-sent-ts=1507246572675;turbo=1;user-id=1337;user-type=global_mod :ronni!ronni@ronni.tmi.twitch.tv PRIVMSG #ronni :Kappa Keepo Kappa
PING :tmi.twitch.tv
:tmi.twitch.tv 001 justinfan12345 :Welcome, GLHF!
```

The RFC 2812 ABNF is authoritative for prefix, command, params, and trailing ([RFC 2812][rfc2812] §2.3.1). `modern.ircdocs.horse` restates the same grammar with a `source` name instead of `prefix` ([modern IRC][modern-irc], "Message Format").

### 3.3 Registration

A general IRC client registers in this order:

```text
CAP LS 302                 # discover capabilities; registration is held
PASS <password>            # optional; must precede NICK/USER
NICK <nickname>
USER <user> 0 * :<realname>
CAP REQ :<capabilities>    # request extensions
CAP END                    # release registration
```

On success the server sends `RPL_WELCOME` (001), then 002-004 with server information, 005, and the MOTD (375 start, 372 lines, 376 end). Modern IRC calls 005 `RPL_ISUPPORT`; RFC 2812 assigned 005 to `RPL_BOUNCE`, so treat the token list as an extension ([modern IRC][modern-irc], "RPL_ISUPPORT"). RFC 2812 requires `PASS` before the `NICK`/`USER` pair when a password is used, and recommends PASS, NICK, USER ([RFC 2812][rfc2812] §3.1). The `USER` command supplies the username and real name; Twitch's documented handshake omits it, showing `PASS oauth:<token>` then `NICK <lowercase-login>` ([Twitch IRC concepts][twitch-irc], "Authenticating"). Whether Twitch requires, ignores, or rejects `USER` is **UNKNOWN**; the prototype should use the documented minimal handshake and try `USER` only if registration fails.

`005` `RPL_ISUPPORT` advertises server tokens such as `CHANTYPES=#`, `PREFIX=(ov)@+`, `CASEMAPPING`, `NETWORK`, and limits. Parse it tolerantly and do not hard-code a server's token set ([modern IRC][modern-irc], "RPL_ISUPPORT").

### 3.4 Capability negotiation

`CAP` is the IRCv3 extension mechanism. It exists so clients and servers can enable features without breaking older peers: registration is paused after `CAP LS`/`CAP REQ` until the client sends `CAP END` ([IRCv3 capability negotiation][ircv3-cap]).

| Subcommand | Direction | Purpose |
|---|---|---|
| `CAP LS [version]` | Client -> server | List available capabilities. With `302`, the server may send values (`sasl=PLAIN,EXTERNAL`) and multiline replies using `*`. |
| `CAP REQ :<caps>` | Client -> server | Request a set of capabilities. The set is accepted or rejected as a whole. |
| `CAP ACK :<caps>` | Server -> client | Acknowledge; capabilities are enabled. |
| `CAP NAK :<caps>` | Server -> client | Reject; no change is made. |
| `CAP END` | Client -> server | Finish negotiation and release registration. |
| `CAP LIST` | Client -> server | List enabled capabilities. |
| `CAP NEW :<caps>` | Server -> client | A new capability is available (CAP 302 / `cap-notify`). |
| `CAP DEL :<caps>` | Server -> client | A capability is no longer available (CAP 302 / `cap-notify`). |

`CAP LS 302` also implicitly enables `cap-notify`, which is why `NEW`/`DEL` can arrive later ([IRCv3 capability negotiation][ircv3-cap], "cap-notify"). Capability names are case-sensitive opaque identifiers. Twitch's documented capabilities are `twitch.tv/tags`, `twitch.tv/commands`, and `twitch.tv/membership`; requesting tags requires commands ([Twitch IRC concepts][twitch-irc], "Requesting Twitch Capabilities").

For StreamKeepr, the minimum request is:

```text
CAP REQ :twitch.tv/tags twitch.tv/commands
```

Do **not** request `twitch.tv/membership`: it adds JOIN/PART noise for channels under 1,000 users, and it is unnecessary for message capture. If Twitch NAKs the pair, retry `twitch.tv/commands` alone only if the design is willing to operate without tags; this memo treats tags as required because `id`, `tmi-sent-ts`, `user-id`, `badges`, and `emotes` are the capture schema. A NAK should therefore mark chat unavailable, not silently degrade.

### 3.5 Core commands and numerics

| Command | Direction | Purpose | Capture relevance |
|---|---|---|---|
| `PING` / `PONG` | Server -> client / client -> server | Liveness. The client must echo the PING payload. | Must answer or Twitch closes the connection. |
| `JOIN` / `PART` | Client -> server / both | Join or leave a channel. On join, the server sends `JOIN`, `353`, `366`, and (with commands) `USERSTATE`/`ROOMSTATE`. | Join active channels; PART when a session ends. |
| `PRIVMSG` | Both | A channel or private message. | The primary chat message. |
| `NOTICE` | Both | An out-of-band notice, often an error or service message. | Join failures, auth failures, rate limits. |
| `QUIT` | Both | A user leaves the network. | Membership noise; not a chat message. |
| `MODE` | Both | Channel/user modes. | Twitch does not expose general modes; ignore. |
| `TOPIC` | Both | Channel topic. | Twitch metadata; not needed for replay. |
| `NAMES` | Client -> server; `353`/`366` replies | List channel members. | Join-time only; not needed. |
| `WHO` / `WHOIS` | Client -> server | User/host queries. | Not needed. |
| `KICK` | Both | Remove a user from a channel. | Twitch uses `CLEARCHAT`; ignore. |
| `INVITE` | Both | Invite a user to a channel. | Not needed. |

Important numerics:

| Numeric | Name | Meaning |
|---|---|---|
| `001` | `RPL_WELCOME` | Registration complete. |
| `002`-`004` | `RPL_YOURHOST`, `RPL_CREATED`, `RPL_MYINFO` | Server information. |
| `005` | `RPL_ISUPPORT` (modern IRC; `RPL_BOUNCE` in RFC 2812) | Server tokens and limits. |
| `353` | `RPL_NAMREPLY` | Channel member list (one or more lines). |
| `366` | `RPL_ENDOFNAMES` | End of member list. |
| `372` | `RPL_MOTD` | MOTD line. |
| `375` / `376` | `RPL_MOTDSTART` / `RPL_ENDOFMOTD` | MOTD boundaries. |
| `421` | `ERR_UNKNOWNCOMMAND` | Unsupported command. |
| `433` | `ERR_NICKNAMEINUSE` | Nickname already in use. |
| `451` | `ERR_NOTREGISTERED` | Command before registration. |
| `464` | `ERR_PASSWDMISMATCH` | Bad password. |

The numeric definitions are in RFC 2812 §5 ([RFC 2812][rfc2812]). Twitch's `001`-`004`, `375`/`372`/`376` welcome sequence is shown verbatim in its IRC concepts page ([Twitch IRC concepts][twitch-irc], "Authenticating"). Twitch may quietly drop a `JOIN` for a nonexistent channel; for a suspended/deleted channel it sends a `NOTICE` with `msg-id=msg_channel_suspended` ([Twitch IRC concepts][twitch-irc], "Joining and Leaving Chatrooms").

### 3.6 IRCv3 tags

Tags are per-message metadata: `@key=value;key2=value2`. The tag block precedes the prefix and command. The format is defined by the IRCv3 message-tags specification ([IRCv3 message tags][ircv3-tags]). Key points:

- Tag order is not meaningful. Duplicate keys: the last occurrence wins.
- Tag keys are case-sensitive opaque identifiers; unknown tags must be ignored, not rejected.
- Empty values (`foo=`) are equivalent to missing values (`foo`).
- Values are UTF-8. Invalid UTF-8 may be dropped, but must not be replaced with bytes that could collide.
- Client-only tags are prefixed with `+`; servers must relay them and must treat them as untrusted.

Escaping:

| Character | Escaped form |
|---|---|
| `;` | `\:` |
| space | `\s` |
| `\` | `\\` |
| CR | `\r` |
| LF | `\n` |

Split on `;` **before** unescaping. A trailing lone `\` should produce no character; an invalid escape such as `\b` should drop the backslash and keep `b` ([IRCv3 message tags][ircv3-tags], "Escaping values").

The extensions relevant to a chat capture client:

| Capability/tag | Purpose | Twitch |
|---|---|---|
| `message-tags` | Enables the full tag format and `TAGMSG`. | Twitch sends tags via `twitch.tv/tags`; it does not document `message-tags`. Treat Twitch's tags as IRCv3-format tags. |
| `server-time` / `time` | Server timestamp in `YYYY-MM-DDThh:mm:ss.sssZ` UTC. | Not documented; Twitch uses `tmi-sent-ts` (epoch ms) instead. Do not rely on `time`. |
| `message-ids` / `msgid` | Server-unique message ID. | Not documented; Twitch uses `id` (UUID) instead. Do not rely on `msgid`. |
| `account-tag` / `account` | Authenticated account name. | Not documented; Twitch uses `user-id`, `display-name`, and the prefix. |
| `batch` | Groups related messages (e.g. history). | Not documented/supported by Twitch. |
| `labeled-response` | Correlates a response with a client request. | Not needed for read-only capture. |
| `echo-message` | Echoes the client's own messages back. | Not needed; StreamKeepr does not send chat. |
| `chathistory` | `CHATHISTORY` queries for stored messages. Work-in-progress/draft. | Not supported/documented by Twitch. |
| `read-marker` | Tracks read position across clients. | Not needed. |
| `monitor` | Notifies when a nickname comes online/offline. | Not needed. |
| `extended-join` | JOIN includes account and real name. | Not needed; Twitch membership is limited. |
| `away-notify` | Notifies when users go away/back. | Not needed. |
| `account-notify` | Notifies when users log in/out of accounts. | Not needed; Twitch identity is per message. |

The tag specifications are at [server-time][ircv3-servertime], [message-ids][ircv3-msgid], [batch][ircv3-batch], [chathistory][ircv3-chathistory], and the IRCv3 spec index ([IRCv3 specs][ircv3-specs]). "Widely deployed" is not the same as "required": these are opt-in, and a client must feature-detect via `CAP`. Twitch exposes a small, fixed subset, so the parser should be written for Twitch's tags first and tolerate unknown IRCv3 tags.

### 3.7 SASL

SASL is the IRCv3 authentication extension. A client negotiates `sasl`, sends `AUTHENTICATE <MECHANISM>` (commonly `PLAIN` or `EXTERNAL`), the server replies `AUTHENTICATE +`, the client sends base64 data in 400-byte chunks, and the server reports:

| Numeric | Meaning |
|---|---|
| `900` | Logged in (account name, mask, account). |
| `903` | SASL authentication successful. |
| `904` | SASL authentication failed. |
| `905` | SASL message too long. |
| `906` | SASL aborted. |
| `907` | Already authenticated. |

The mechanism and numeric definitions are in the IRCv3 SASL specification ([IRCv3 SASL][ircv3-sasl]). Twitch does **not** document `sasl`; it uses `PASS oauth:<token>` instead. A Twitch client should not send `AUTHENTICATE`.

### 3.8 Delivery, ordering, and disconnects

Base IRC provides no delivery guarantees. The protocol is asynchronous, servers relay through a spanning tree, and there is no acknowledgement or exactly-once mechanism ([RFC 1459][rfc1459] §2.3, §8.3). In practice:

- **Ordering:** not guaranteed. Twitch explicitly says it "does not guarantee the order of the messages" ([Twitch IRC concepts][twitch-irc], "Parsing messages").
- **Duplicates:** possible. Twitch says it "may also send a message multiple times if it doesn't think the chatbot received it" ([Twitch IRC concepts][twitch-irc], "Parsing messages"). IRCv3 `msgid`/Twitch `id` is the dedupe key.
- **Replay:** none unless the server supports `chathistory`. Twitch does not. A reconnect shows only new messages.
- **Liveness:** the server sends `PING`; the client must reply `PONG` with the same payload or the connection is terminated ([Twitch IRC concepts][twitch-irc], "Keepalive Messages"). A general IRC server may use a different interval; the client should treat any inbound traffic as liveness.
- **Disconnect:** `QUIT` broadcasts a user leaving; `ERROR` carries a server-side close reason. On `RECONNECT`, Twitch says the normal action is to reconnect and rejoin the channels; the time before the server closes is indeterminate ([Twitch IRC concepts][twitch-irc], "RECONNECT Command").
- **Bouncers:** ZNC/soju provide history by staying connected and buffering; that is an external service, not protocol behavior ([ZNC][znc]). StreamKeepr is the bouncer equivalent for its own recordings, but it cannot recover messages missed while it was offline.

### 3.9 Message size and rate limits

- The legacy line limit is 512 bytes including CRLF; IRCv3 adds up to 8191 bytes for the tag block ([RFC 1459][rfc1459] §2.3; [IRCv3 message tags][ircv3-tags]).
- Servers enforce their own send-queue and rate policies; RFC 1459 describes a FIFO send queue and closing slow connections rather than blocking ([RFC 1459][rfc1459] §8.3-§8.4).
- Twitch's documented limits are in §4.5. For read-only capture, the relevant ones are join and authentication limits, not message-send limits.
- A parser must impose its own bounds regardless of protocol limits: maximum line length, tag count, tag key/value length, parameter count, and message text length. Never let an untrusted peer grow an unbounded buffer.

### 3.10 IRCv3 versus legacy IRC

Legacy IRC (RFC 1459/2812) is the interoperable core: registration, channels, `PRIVMSG`, `NOTICE`, `PING`/`PONG`, numerics. IRCv3 is a set of opt-in extensions negotiated with `CAP`. A client that ignores `CAP` still works on a legacy server; a client that requests a capability must handle `ACK`/`NAK` and `NEW`/`DEL`.

Deployment varies by network. `message-tags`, `server-time`, `msgid`, `account-tag`, `batch`, `away-notify`, `account-notify`, `extended-join`, and `monitor` are widely implemented by modern servers, but none is guaranteed. `chathistory` and `read-marker` are newer and often draft. Twitch's subset is smaller than a general IRCv3 network: tags and Twitch commands only. The safe design is feature detection plus graceful degradation, but for Twitch the feature set is fixed enough to require `twitch.tv/tags` and fail closed if it is unavailable.

## 4. Twitch IRC specifics

### 4.1 Endpoints and authentication

| Item | Value | Source |
|---|---|---|
| Secure WebSocket | `wss://irc-ws.chat.twitch.tv:443` | [Twitch IRC concepts][twitch-irc] |
| Secure TCP/TLS | `irc://irc.chat.twitch.tv:6697` | [Twitch IRC concepts][twitch-irc] |
| Plaintext TCP | `irc://irc.chat.twitch.tv:6667` (still listed; not recommended) | [Twitch IRC concepts][twitch-irc] |
| Non-secure WebSocket | `ws://irc-ws.chat.twitch.tv:80` (decommissioned 2025-08-15) | [decommission announcement][twitch-ws-decommission] |
| Auth | `PASS oauth:<user-access-token>` then `NICK <lowercase-login>` | [Twitch IRC concepts][twitch-irc] |
| Read scope | `chat:read` | [Twitch IRC concepts][twitch-irc] |
| Send scope | `chat:edit` | [Twitch IRC concepts][twitch-irc] |
| Token type | User access token; an app access token cannot read chat as a user | [Twitch chat auth][twitch-chat-auth] |
| Anonymous | `NICK justinfan<digits>` with no PASS; undocumented | [Chatterino source][chatterino]; [2025 staff reply][justinfan-2025] |

Twitch's documented handshake is minimal:

```text
PASS oauth:<token>
NICK <login>
CAP REQ :twitch.tv/tags twitch.tv/commands
```

The `CAP REQ` may be sent before or after `PASS`/`NICK` per IRCv3, but Twitch's docs do not pin the order; send `CAP LS 302` first and confirm in the prototype. The standard `CAP LS 302` discovery step is not shown in Twitch's docs but is valid IRCv3. The prototype should record Twitch's actual `LS`/`ACK` responses. Twitch's `001` reply is `:tmi.twitch.tv 001 <user> :Welcome, GLHF!`; a bad login is `:tmi.twitch.tv NOTICE * :Login authentication failed`, and wrong ordering is `:tmi.twitch.tv NOTICE * :Improperly formatted auth` ([Twitch IRC concepts][twitch-irc], "Authenticating").

Anonymous access is the weak point. Twitch's docs require a user token; `justinfan` is not documented. Chatterino's current source hardcodes `justinfan64537` ([Chatterino username][chatterino], `TwitchCommon.hpp:14`) and omits `PASS` for the anonymous account ([Chatterino connection][chatterino-irc], `TwitchIrcServer.cpp:335-338`). A Twitch staff reply in June 2025 said anonymous IRC "Should still work" while recommending broadcaster permission ([2025 staff reply][justinfan-2025]). A 2024 forum reply noted `justinfan` "was never officially documented" and that its limits were unknown ([2024 staff reply][justinfan-undoc]). **UNKNOWN:** whether anonymous access still works in November 2026 at the needed rate, and what limits apply. This is the first prototype item.

### 4.1.1 Anonymous access: evidence and common recommendations

Twitch's current IRC page requires a user token and does not mention `justinfan`: "you must authenticate ... with `chat:read` ... You will need this token to continue" ([Twitch IRC concepts][twitch-irc]); the chat overview says chat clients "will need to authenticate ... to read and write" ([Twitch chat overview][twitch-chat]). The changelog has no `justinfan` mention or anonymous-IRC entry, while 2026 entries still update IRC tags ([Twitch changelog][twitch-changelog]). That is a documentation gap, not a removal notice. This review is documentary: no Twitch IRC connection was made, so "still works" below means current clients and services still ship the path, not that it was live-tested here.

| Source | Date | Claim | Reliability |
|---|---|---|---|
| [Twitch IRC concepts][twitch-irc], [chat overview][twitch-chat] | fetched 2026-11-13 | `PASS oauth:<user-token>` with `chat:read` is required; `justinfan` is absent | Official (docs) |
| [2024 join-limit announcement][join-limits], jbulava (Twitch staff) | 2024-02-02 | IRC read access requires "a user who is allowed to read chat in a channel"; 100 concurrent joins per user; anonymous not addressed | Official (staff announcement) |
| [2025 justinfan thread][justinfan-2025], Barry Carlyon (Twitch staff) | 2025-06-11 | Anonymous IRC "Should still work"; "the optimal solution is to get permission from the broadcaster to be present" | Official (staff reply) |
| [2024 thread][justinfan-undoc], Dist | 2024-02-07 | "Justinfan was never officially documented"; impact on justinfan connections "not yet known" | Community (not staff) |
| [Chatterino][chatterino] / [connection code][chatterino-irc] | retrieved 2026-11-13 | Widely used third-party client; hardcodes `justinfan64537`; omits `PASS` for anonymous accounts; files changed in 2026 | Maintainer (client) |
| [tmi.js docs][tmi-js] / [source][tmi-js-source] | docs 2023-06-05; source fetched 2026-11-13 | Omit `identity` for anonymous; random `justinfan<digits>` plus dummy `PASS SCHMOOPIIE`; "ideal for chat overlays" | Maintainer docs (stale) |
| [twitch-irc 6.1.3][twitch-irc-crate] / [login source][twitch-irc-login] | 2026-10-01 | Default config is anonymous (`justinfan12345`, no `PASS`); 90-channel library default per connection | Maintainer (library) |
| [recent-messages2][recent-messages2] | pushed 2026-10-21 | Public production service uses the anonymous default and pooled connections | Community (production service) |
| [chat-downloader][chat-downloader-irc]; [TwitchLib #1086][twitchlib-1086] | 2025-11-05; 2022-05-15 | Dummy `PASS` + `justinfan67420`; `ConnectionCredentials("justinfan12345", "")`; no limits discussed | Maintainer sources |

**Documented facts.** Only the token path is documented. The limits Twitch states (100 concurrent joins, 20 joins/10 s, 20 auth/10 s) are per user; anonymous connections have no authenticated user identity, so their application is **UNKNOWN**, and the 2024 thread did not resolve it. The 2024 "never officially documented" quote is Dist's, not a staff statement; the forum record marks that account `staff=false`. None of these client or library sources documents an anonymous limit or a production prohibition; their silence is not permission.

**Community practice.** Use `NICK justinfan<random digits>`; randomization is cheap and handles `433`, although fixed nicks (`64537`, `12345`, `67420`) are used by current clients, so uniqueness does not appear to be enforced. Omit `PASS`, or send a dummy (`PASS SCHMOOPIIE`, `nopass`, etc.); both appear in current clients, and a dummy is a compatibility fallback, not a documented requirement. `CAP REQ :twitch.tv/tags twitch.tv/commands` first, then optional `PASS`, then `NICK` is the common order ([tmi.js source][tmi-js-source]; [twitch-irc handshake][twitch-irc-handshake]); Twitch does not document anonymous handshake order, and the documented token rule is only `PASS` before `NICK`. Pace joins conservatively (Chatterino 18 per 12.5 s; tmi.js 2 s between joins; twitch-irc's 90-channel library default), handle `RECONNECT`/backoff/gaps, do not impersonate a real login, and do not treat the anonymous nick as a stable identity. For a documented, stable identity, a dedicated token is the only documented option; anonymous is reasonable only as an explicitly best-effort read path with a token fallback.

Sub-only, followers-only, and verified-only read behavior is **UNKNOWN**: no source documents it, and it was not tested. Phone verification is documented as a send-side gate, so it likely does not block reads, but do not rely on that. We found no credible 2025–2026 report of a global anonymous-IRC break or a documented anonymous rate limit; current 2026 client code and recent-messages2 show continued use, but that is absence of evidence, not a guarantee.

**Implication for StreamKeepr.** The evidence supports the memo's current anonymous-default plus token-fallback recommendation; it does not support making the token mandatory. A maintained 2026 library defaults to anonymous, a public production service uses it, and no credible 2025–2026 break was found. But the official path still requires a token, anonymous limits and restricted-room behavior remain **UNKNOWN**, and no source offers durability. The existing prototype/soak and token fallback remain the evidence-supported path; anonymous should be treated as best-effort, not a documented contract.

### 4.2 Capabilities

Twitch documents three capabilities ([Twitch IRC concepts][twitch-irc], "Requesting Twitch Capabilities"):

| Capability | Effect | Use in StreamKeepr |
|---|---|---|
| `twitch.tv/tags` | Adds metadata tags to commands and membership messages. Requires `twitch.tv/commands`. | **Required**: `id`, `tmi-sent-ts`, `user-id`, `display-name`, `badges`, `emotes`, `reply-*`. |
| `twitch.tv/commands` | Enables Twitch-specific commands (`CLEARCHAT`, `CLEARMSG`, `USERNOTICE`, `ROOMSTATE`, `RECONNECT`, `USERSTATE`, `GLOBALUSERSTATE`). | **Required**: moderation events and room state. |
| `twitch.tv/membership` | Enables `JOIN`/`PART` for users in channels under 1,000 users ([Twitch IRC migration][twitch-migration]). | **Not needed**; adds noise and is incomplete for large channels. |

Twitch does not document `sasl`, `chathistory`, `batch`, `labeled-response`, `echo-message`, `read-marker`, `monitor`, `extended-join`, `away-notify`, or `account-notify` for IRC. Some may be advertised; the client should ignore unknown capabilities and not depend on them.

### 4.3 Tags

Twitch's PRIVMSG tags are the capture schema. The table below is condensed from the official tag reference ([Twitch IRC concepts][twitch-irc], "PRIVMSG Tags"); the full list includes `client-nonce`, `flags`, `returning-chatter`, `gifs`, `source-only`, and `vip`.

| Tag | Meaning | Capture use |
|---|---|---|
| `tmi-sent-ts` | Server timestamp. Twitch's docs call it a "UNIX timestamp"; every documented example is a 13-digit millisecond epoch. Treat as milliseconds and validate the range. | `at`; ordering; gap boundaries. |
| `id` | Unique message UUID. | Primary key; dedupe key. |
| `room-id` | Channel/broadcaster ID. | Route/validate; optional metadata. |
| `user-id` | Sender's Twitch user ID. | Deletion by user; personal data. |
| `display-name` | Display name, IRCv3-escaped; may be empty. | Rendering; fallback to login. |
| `color` | Hex RGB (`#RRGGBB`); may be empty. | Name color; validate and contrast-check. |
| `badges` | Comma-separated `set/version` badges. | Text chips in v1; images later. |
| `badge-info` | Subscriber-month metadata for badges. | Optional. |
| `emotes` | `id:start-end,start-end/...`; zero-based inclusive code-point ranges. | Emote rendering. |
| `bits` | Bits cheered; only on cheer messages. | Optional; store `null` or integer. |
| `first-msg` | First message from this user in the channel (`1`/`0`). | Optional UI. |
| `reply-parent-*` | Parent message ID, user ID/login/display name, body; thread-parent fields. | Reply rendering. |
| `source-*` | Shared-chat source message ID, room ID, badges, badge info. | Deferred; can dedupe cross-channel shared chat later. |
| `mod`, `subscriber`, `turbo`, `vip`, `user-type` | Role flags. | Text chips; do not rely on color alone. |

`CLEARCHAT` adds `target-user-id` (optional) and `ban-duration` (timeouts); `CLEARMSG` adds `target-msg-id` and `login` (the deleted message's author, not the moderator); `USERNOTICE` adds `msg-id`, `system-msg`, and `msg-param-*`; `ROOMSTATE` adds `emote-only`, `followers-only`, `r9k`, `slow`, `subs-only` ([Twitch IRC concepts][twitch-irc], command references).

`rm-received-ts` is **not** a Twitch tag. It is added by the third-party Recent Messages API to every historical message, in milliseconds since the Unix epoch, alongside `historical=1`; messages deleted by moderation additionally carry `rm-deleted=1` ([Recent Messages API][recent-messages]). Do not expect it on live IRC.

### 4.4 Commands

| Command | Direction | What it means | Capture action |
|---|---|---|---|
| `PRIVMSG` | Server -> client | A chat message. | Store. |
| `CLEARCHAT` | Server -> client | All messages cleared, or all messages from `target-user-id` cleared. | Store `clear_all` or `clear_user`. |
| `CLEARMSG` | Server -> client | One message deleted (`target-msg-id`). | Store `delete`. |
| `USERNOTICE` | Server -> client | Sub/resub/subgift/raid/etc. with `msg-id`, `system-msg`, `msg-param-*`. | Defer in v1; optionally store as a system event. |
| `NOTICE` | Server -> client | Join/auth/rate-limit errors, `msg-id`. | Handle `msg_channel_suspended`, `msg_banned`, auth failures; ignore informational notices. |
| `ROOMSTATE` | Server -> client | Chat settings on join or change. | Store optional room-state event; not a chat row. |
| `RECONNECT` | Server -> client | Server will terminate the connection; reconnect and rejoin. | Reconnect immediately; no replay. |
| `JOIN` / `PART` | Server -> client (membership) | Membership changes, only reliably under 1,000 users. | Ignore for capture. |
| `USERSTATE` / `GLOBALUSERSTATE` | Server -> client | The authenticated user's state. | Ignore; do not store. |
| `WHISPER` | Historically server/client | Direct user-to-user whisper. The current IRC Concepts command table no longer lists it; Twitch directs whispers to Helix/EventSub. | Ignore. |

Twitch's current command reference lists `CLEARCHAT`, `CLEARMSG`, `GLOBALUSERSTATE`, `NOTICE`, `PART`, `PING`, `PRIVMSG`, `RECONNECT`, `ROOMSTATE`, `USERNOTICE`, and `USERSTATE` ([Twitch IRC concepts][twitch-irc], "IRC Command Reference"). `WHISPER` appears in older changelog entries ([Twitch changelog][twitch-changelog], 2020) but not in the current command table; treat it as legacy and out of scope.

### 4.5 Rate and connection limits

| Limit | Value | Notes |
|---|---|---|
| Concurrent channel joins | **100 per user** (since May 2024) | Joining as broadcaster/moderator does not count; authorized EventSub subscriptions do not count. |
| Join attempts | **20 per 10 s per user** | Verified bots: 2,000 per 10 s. |
| Authentication attempts | **20 per 10 s per user** (IRC only) | Verified bots: 200 per 10 s. |
| Message sends | 20 per 30 s regular; 100 per 30 s broadcaster/mod/VIP; 1 per second per channel | Irrelevant to read-only capture. |
| Large channels | JOIN/PART may be omitted above 1,000 users | `PRIVMSG` still arrives; do not depend on membership ([Twitch IRC migration][twitch-migration]). |
| Anonymous identity | **UNKNOWN** | No documented limits; prototype and observe. |

Sources: [Twitch chat overview][twitch-chat] "Rate Limits"; [2024 join-limit announcement][join-limits]. For authenticated users, the 100 limit is per **user**, not per connection; opening more connections does not bypass it. Anonymous behavior is **UNKNOWN**. That is a critical correction to the common "shard by connection" assumption.

### 4.6 No history, no replay

Twitch IRC has no history API. A reconnect shows only new messages; `RECONNECT` tells the client to reconnect and rejoin, not to resume from an offset ([Twitch IRC concepts][twitch-irc], "RECONNECT Command"). There is no `CHATHISTORY` support. The only history-adjacent options are:

- **Recent Messages API** (third party): up to ~800 recent messages, raw IRC lines, `historical=1`, `rm-received-ts`; deleted messages additionally carry `rm-deleted=1` ([Recent Messages API][recent-messages]). Useful as a fallback/backfill for a short gap, not as a full archive.
- **VOD chat GQL** (unofficial): post-stream only, requires the VOD and enabled chat; see `docs/research/twitch-chat-replay.md:96-104`. Not a live path.

### 4.7 Deprecation risk and why IRC still fits

Twitch recommends EventSub and the Twitch API over IRC, and says IRC is more complicated to parse and limited ([Twitch IRC concepts][twitch-irc]). In practice, IRC is deprecated in guidance but not formally sunset. No shutdown announcement was found as of 2026-11-13, and Twitch continued adding IRC tags in 2026 ([Twitch changelog][twitch-changelog], 2026-06-12 and 2026-07-17). The risk is guidance-driven atrophy, not an immediate removal.

EventSub chat is the better product surface, but its authorization model is the blocker. `channel.chat.message` requires `user:read:chat` from a user allowed to read the channel; app-token/conduit access requires `user:bot` plus `channel:bot` from the broadcaster or moderator standing ([Twitch chat auth][twitch-chat-auth]; `docs/research/twitch-chat-replay.md:81-94`). StreamKeepr cannot obtain broadcaster authorization for arbitrary channels. IRC requires only a user who can read public chat. That is why IRC remains the practical arbitrary-channel read path.

## 5. Using IRC in StreamKeepr

### 5.1 Transport: IRC over WSS vs IRC over TCP/TLS

| Criterion | IRC over WSS | IRC over TCP/TLS |
|---|---|---|
| Endpoint | `wss://irc-ws.chat.twitch.tv:443` | `irc://irc.chat.twitch.tv:6697` |
| .NET API | `ClientWebSocket` (already used for EventSub) | `TcpClient` + `SslStream` |
| TLS | Platform HTTP stack; certificate validation on by default | `SslStream.AuthenticateAsClientAsync`; supply a validation callback that accepts only `SslPolicyErrors.None` and rejects every other value |
| Proxy | `ClientWebSocketOptions.Proxy`; HTTP proxy support | Manual HTTP CONNECT; more code and more failure modes |
| Firewall/egress | 443; usually allowed from containers | 6697; sometimes blocked by corporate/cloud egress |
| Framing | WebSocket frames; one frame may contain multiple CRLF lines; buffer and split | Raw byte stream; buffer and split |
| Testability | Fake `IIrcSocket`; in-process Kestrel WebSocket endpoint | Fake `Stream`/socket; simple line tests |
| Code reuse | High: mirror `IEventSubSocket`/`ClientWebSocketEventSubSocket` | Low: new TLS/proxy/line-reader stack |
| Failure modes | WebSocket handshake, close, proxy, frame fragmentation | TCP connect, TLS handshake, proxy, certificate, line buffering |
| Twitch documentation priority | Primary WebSocket path | Listed and supported |
| Verdict | **Recommended for v1** | Defer until a concrete need appears |

**Recommendation: WSS.** The deciding factors are deployment robustness and code reuse. Port 443 is the most likely egress path for a self-hosted container; `ClientWebSocket` already exists in the repo with a fake-socket seam (`src/StreamKeepr/Infrastructure/Twitch/ClientWebSocketEventSubSocket.cs:7-24`); and the TLS/certificate/proxy behavior is the platform's, not hand-rolled. The cost is that WebSocket framing is not IRC line framing: the parser must still find CRLF boundaries inside frames and carry a partial line across frames.

TCP/TLS is not wrong; it is simply more code for the same result. It becomes attractive if StreamKeepr ever needs a generic IRC client for non-Twitch networks, or if a user's egress blocks 443 but allows 6697 (rare). Do not build both in v1. If it is added later, put it behind `IIrcSocket` and a `Chat:Transport` flag; do not fork the parser or state machine.

### 5.2 Authentication: anonymous vs dedicated token

| Option | What it needs | Pros | Cons | Verdict |
|---|---|---|---|---|
| Anonymous `justinfan<digits>` | `NICK justinfan<random>`; no PASS | No secret; no admin dependency; works for public channels in practice | Undocumented; limits **UNKNOWN**; restricted rooms **UNKNOWN**; no stable identity | **Default, conditional on prototype** |
| Dedicated chat user token | `Chat:TwitchUserOAuthToken` with `chat:read`; `NICK <login>` | Documented; stable identity; known scopes; can be validated | Secret to manage; token expiry; account risk; still subject to 100-join limit | **Fallback/config option** |
| Admin OAuth token | Add `chat:read` to the admin sign-in flow | No second secret | Couples chat to admin sign-in; logout would stop capture; changes `docs/twitch-auth-contract.md` | Reject |
| App access token | Client credentials | No user | App tokens cannot read chat as a user | Reject |

The existing memo already recommends a separate `Chat:TwitchUserOAuthToken` rather than an admin-scope change (`docs/research/twitch-chat-replay.md:486`). Keep that boundary. Validate the token at startup with `/oauth2/validate` (login, client ID, scopes, expiry) ([Twitch validate tokens][twitch-validate]); on failure, log the login and reason, never the token, and fall back to anonymous if configured. Do not log `PASS` lines or the token in exceptions. Store the secret through user-secrets or `/run/secrets`, consistent with `docs/hosting-contract.md:131`.

If the no-PASS anonymous handshake is rejected, try a dummy `PASS` once before declaring failure; the prior memo also leaves this as a prototype decision (`docs/research/twitch-chat-replay.md:487`). Do not retry a bad token repeatedly; that burns the 20 auth/10 s budget.

Do **not** reuse `Recorder:TwitchUserOAuthToken`: that token is a Streamlink browser token for subscriber-only streams (`src/StreamKeepr/Configuration/RecorderOptions.cs:17-18`), not a chat token. Separate concerns, separate config key. If `Chat:TwitchUserOAuthToken` is set, require `Chat:TwitchLogin` and verify that the token's login matches it at startup; a mismatch is a configuration error, not a retry loop.

### 5.3 Connection topology

| Option | Joins | Failure isolation | Complexity | Verdict |
|---|---|---|---|---|
| One multiplexed connection | All active channels, up to 100 | One socket failure loses all channels; reconnect restores all | Low | **Recommended v1** |
| One connection per channel | One per channel | Isolated, but N authentications and N sockets; 20 auth/10 s limits; wasteful | High | Reject |
| Shards of N connections | Split channels across sockets | Better isolation; still one user identity, so the 100-join cap is shared | Medium | Only if measured instability or >100 channels with authorization; does not bypass the cap |

- **One connection** is right for the expected self-hosted scale (a handful to a few dozen channels). It keeps one join queue, one auth attempt, one backoff state, and one place to dedupe.
- **Join queue:** one `JOIN` every ~600 ms (≤ 16.7 per 10 s, under the 20/10 s limit). Batch multiple channels per `JOIN #a,#b` only if the limiter accounts for each channel; assume each channel in a multi-channel `JOIN` counts as one attempt and confirm in the prototype. Prefer one channel per command for clear acknowledgements.
- **Priority:** join channels with an active recording first; defer channels that are paused or have no active session. Recompute on `RecordingLibrary.Changed`/`DvrStatus.Changed`.
- **Cap:** stop at 100 joined channels. If more are active, leave the excess unjoined and surface `Chat: 100-channel limit`; do not open a second connection to bypass it. For authenticated users the 100 cap is per user, not per connection ([2024 join-limit announcement][join-limits]); anonymous behavior is **UNKNOWN**.
- **Leave/rejoin:** `PART` a channel when its recording session finalizes and no other session for that channel is active. On app restart, rejoin active sessions. Do not `PART`/`JOIN` on every part; parts belong to one session.
- **Pause/resume:** a paused channel is not joined. Resume joins it after the next recording starts. This keeps the connection below the cap and avoids capturing chat with no media.
- **>100 channels:** the only sanctioned ways to exceed the cap are broadcaster/moderator standing or EventSub authorization. Multiple Twitch identities would multiply the cap but are an abuse/policy risk and are not recommended. This is an open question for the owner.

### 5.4 Connection state machine

```text
Disabled ──Chat:Enabled=false──────────────────────────────────────► Disabled
Idle ──any active recording session───────────────────────────────► Connecting
Connecting ──socket open──────────────────────────────────────────► Registering
Connecting ──timeout/error────────────────────────────────────────► Backoff
Registering ──001 welcome─────────────────────────────────────────► Joining
Registering ──433/464/auth NOTICE─────────────────────────────────► Unavailable
Registering ──timeout/error───────────────────────────────────────► Backoff
Joining ──JOIN/ROOMSTATE/366 or first PRIVMSG─────────────────────► Live
Joining ──join timeout (per channel)──────────────────────────────► Joining (mark channel unavailable)
Live ──PING────────────────────────────────────────────────────────► Live (PONG)
Live ──PRIVMSG/CLEARMSG/CLEARCHAT/ROOMSTATE───────────────────────► Live (capture)
Live ──RECONNECT───────────────────────────────────────────────────► Backoff (immediate)
Live ──close/error/idle timeout────────────────────────────────────► Backoff
Backoff ──delay────────────────────────────────────────────────────► Connecting
Live/Joining ──NOTICE msg_channel_suspended/msg_banned─────────────► Channel unavailable (keep connection)
Live/Joining ──session finalize / channel removed──────────────────► PART; Idle when no channels
Any ──shutdown─────────────────────────────────────────────────────► Closing
Closing ──flush, close socket──────────────────────────────────────► Disabled/Idle
```

For TCP/TLS, `Connecting` splits into `TcpConnecting` → `TlsHandshake` → `Registering`; the WSS path folds TCP + TLS into `ClientWebSocket.ConnectAsync`. The rest of the state machine is transport-independent. `Registering` covers `CAP LS`/`CAP REQ`, `PASS`/`NICK`, and the wait for `001`; `Connected` is the instant `001` arrives, and `Joining` covers the join queue and per-channel acknowledgements.

Timeouts (client policy, not Twitch guarantees):

| Phase | Timeout | On expiry |
|---|---|---|
| WebSocket/TCP connect | 10 s | Backoff; log endpoint and exception type. |
| TLS handshake (TCP only) | 10 s | Backoff; never retry with validation disabled. |
| Registration (`CAP END` to `001`) | 15 s | Backoff; if `433`/`464`, `Unavailable` instead. |
| Join acknowledgement per channel | 10 s | Mark that channel unavailable; continue others; retry on next session. |
| PING/PONG / idle | 5 min with no inbound traffic | Backoff. Twitch's PING interval is **UNKNOWN**; any inbound line resets the watchdog. |
| Socket close | 3 s | Abort; matches the EventSub socket's close timeout (`src/StreamKeepr/Infrastructure/Twitch/ClientWebSocketEventSubSocket.cs:19`). |
| Reconnect backoff | 5 s, 10 s, 20 s, 40 s, 80 s, 160 s, 300 s cap, ±25% jitter | Reuse `IRetryJitter` (`src/StreamKeepr/Infrastructure/Runtime/RetryJitter.cs:3-26`). Reset after 60 s live. |

`RECONNECT` is special: do not apply the first backoff step; close and reconnect immediately, then rejoin. The server may close at an indeterminate time, so waiting is worse than a controlled reconnect. A small gap is acceptable and must be recorded.

`433` (nickname in use): for anonymous `justinfan<random>`, regenerate the suffix once; for a token identity, mark `Unavailable` and log (the account may already be connected, or the login is wrong). `464` (bad password) and `NOTICE * :Login authentication failed` are configuration errors: `Unavailable`, no retry loop, and no repeated auth attempts (20/10 s limit).

### 5.5 Parser

The parser is a pure, testable pipeline. Keep it independent of sockets and files.

| Layer | Responsibility | Bounds and behavior |
|---|---|---|
| `IrcLineReader` | Bytes -> complete lines. Handles CRLF and LF-only, carries a partial line across reads/frames. | Max 64 KiB per line; overlong lines are dropped and counted, never buffered indefinitely. Empty lines ignored. |
| `IrcTagParser` | `@...` -> `IReadOnlyDictionary<string,string>`. | Max 100 tags; key ≤ 64 chars; value ≤ 8191 bytes; split on `;` before unescaping; duplicate keys last wins; unknown escapes drop the backslash. |
| `IrcMessageParser` | Prefix, command, params, trailing. | Max 32 params (RFC allows 15; be lenient but bounded); command uppercased; prefix parsed as `nick[!user][@host]` or server name; malformed lines yield `null`, never throw. |
| `TwitchChatParser` | Twitch tags/commands -> `TwitchChatEvent`. | Validate IDs, colors, emote ranges, timestamps; map `PRIVMSG`/`CLEARMSG`/`CLEARCHAT`/`ROOMSTATE`/`NOTICE`/`RECONNECT`/`PING`; ignore `USERSTATE`, `GLOBALUSERSTATE`, `JOIN`, `PART`, `WHISPER`, unknown commands. |
| `ChatEvent` | Domain model for capture. | `Message`, `Delete`, `ClearUser`, `ClearAll`, `RoomState`, `Notice`, `Reconnect`, `Ping`, `Unknown`; each carries `At` and `AtSource` (`server`/`local`). |

Message model:

```csharp
public sealed record TwitchChatMessage(
    string Id,
    string Channel,
    long AtMs,
    string AtSource,          // "server" (tmi-sent-ts) or "local" (receive time)
    string? UserId,
    string Login,
    string DisplayName,
    string? Color,
    IReadOnlyList<TwitchBadge> Badges,
    IReadOnlyList<TwitchEmote> Emotes,
    int? Bits,
    TwitchReply? Reply,
    bool Action,
    string Text);
```

Parsing rules that matter:

- **`tmi-sent-ts`:** parse as `long` milliseconds. If absent/invalid, use `TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()` and set `AtSource=local`; this keeps the row but flags its timestamp. Reject values outside a sane range (e.g., before 2010 or more than 24 h in the future) and fall back to local.
- **`emotes`:** format `id:start-end,start-end/id:...`; ranges are zero-based, inclusive, **Unicode code point** indices. Parse with `int`; validate `0 <= start <= end < codePointCount`; if invalid, drop that emote range and log once per message. Do not use UTF-16 indices. In C#, `string.EnumerateRunes()` counts code points; in JS, `Array.from(text)` splits by code point. Twitch's docs say "position indices are zero-based" but do not state the unit; the observed behavior and forum evidence are code points (`docs/research/twitch-chat-replay.md:77`).
- **ACTION:** `/me` arrives as `\u0001ACTION <text>\u0001`. Strip the prefix and suffix, set `Action=true`, and subtract the 8-code-point prefix length from every emote range (the docs say ranges begin after `\u0001ACTION `). If a range would go negative, drop it.
- **Replies:** `reply-parent-msg-id`, `reply-parent-user-id`, `reply-parent-user-login`, `reply-parent-display-name`, `reply-parent-msg-body`; unescape the body (`\s` etc.) and truncate to 500 characters. Store `null` when the parent ID is absent.
- **CLEARMSG:** `target-msg-id` is the deleted message; `login` is the deleted message's author (per Twitch's tag description), not the moderator. Store only the target ID and event time.
- **CLEARCHAT:** presence of `target-user-id` means clear that user's messages; absence means clear all. `ban-duration` means a timeout; store it for diagnostics but the UI only needs the clear.
- **ROOMSTATE:** `emote-only`, `followers-only`, `r9k`, `slow`, `subs-only` are settings, not chat rows. Store an optional event; do not render as a message.
- **NOTICE:** `msg-id=msg_channel_suspended` or `msg_banned` marks the channel unavailable; `msg_ratelimit`/`msg_duplicate` are usually send-side but a join-rate violation may also produce one, so log and back off; auth failures arrive as `NOTICE * :Login authentication failed` with no `msg-id`.
- **Malformed input:** return `null`, increment `parseErrorCount`, log at Warning at most once per minute per error kind. Never throw out of the read loop. A single bad line must not drop the connection.

The parser must never assume `@time` or `@msgid`; Twitch uses `tmi-sent-ts` and `id`. It must also tolerate tag values that are empty, tags in any order, and unknown tags. Cap total line length before parsing; Twitch can send long tag blocks, and a malicious peer (or a bug) must not allocate unbounded memory.

### 5.6 Read loop and backpressure

```text
socket read -> IrcLineReader -> IrcMessageParser -> TwitchChatParser
            -> bounded Channel<TwitchChatEvent> per session
            -> ChatLogStore writer task -> JSONL bucket + events.jsonl
```

- **One reader task per connection.** It must never await disk I/O. Parsing is CPU-only and bounded. For TCP/TLS, wrap the `SslStream` with `PipeReader.Create`; for WSS, feed received frames into the same `IrcLineReader`. The queue between parsing and persistence is a bounded `Channel<TwitchChatEvent>` ([System.IO.Pipelines][pipelines]; [Channels][channels]).
- **Bounded per-session queue**, e.g. 10,000 events. Use `BoundedChannelFullMode.DropWrite` (or `DropOldest`) and increment a `droppedCount`. Do **not** use `Wait`: blocking the socket read lets the server's send queue grow, and a slow client can be disconnected (RFC 1459 describes this behavior; Twitch does not document its send-queue policy). Unbounded is also wrong: a slow disk would grow memory without bound. Chat is best-effort; recording is not.
- **Drop semantics:** when the queue is full, drop the event and remember the first/last dropped timestamp. At the next successful append, write a `dropped` event with `{from,to,count}`; the reader treats it as a gap. Do not silently discard.
- **Writer task per session.** Batch up to 100 events or 1 s, append to the current 10-minute UTC bucket, flush the `StreamWriter` (and underlying `FileStream`) every 1 s or 100 events. `FileShare.ReadWrite | FileShare.Delete` so a concurrent delete/read does not fail; `FileOptions.Asynchronous`.
- **Routing:** `PRIVMSG` carries the channel as its first parameter (`#login`). Map `#login` to the active session; if no session, drop the event (do not create a chat file for a channel that is not recording). `CLEARMSG`/`CLEARCHAT`/`ROOMSTATE` also carry the channel.
- **Ordering:** keep a 1–2 s reorder buffer sorted by `(AtMs, Id)` before appending. This absorbs small out-of-order delivery; it does not guarantee order. Duplicates are filtered by `Id`.
- **Flush on shutdown/finalize:** drain the queue with a 5 s cap, flush, write `end`, close the file. If the drain times out, drop the remainder and record `dropped`; never block shutdown.

### 5.7 Reconnect

- **Backoff:** 5 s, 10 s, 20 s, 40 s, 80 s, 160 s, 300 s cap, ±25% jitter via `IRetryJitter`. Reset after 60 s live or after a successful rejoin of at least one channel.
- **`RECONNECT`:** close and reconnect immediately (no first backoff), rejoin all active channels. Write a `gap` event per channel from the last captured `at` to the reconnect time, then a `reconnect` event with the new `connectionId`.
- **Unexpected close:** write `gap` events, backoff, reconnect, rejoin. Do not replay; there is nothing to replay.
- **Duplicate suppression:** a bounded `HashSet<string>` of recent `id`s per channel (e.g., 4,096) in the capture service; the reader also dedupes by `id` when serving. On restart, seed the set from the tail of the current bucket. Duplicates can arrive after a reconnect or because Twitch retried; never show the same message twice.
- **Auth failure:** do not retry with the same token. If a token is configured, fall back to anonymous once; if anonymous also fails, `Unavailable` and wait for config change/session restart. Respect the 20 auth/10 s limit.
- **Channel failure:** `NOTICE msg_channel_suspended`/`msg_banned` marks that channel unavailable; keep the connection for other channels. Retry the channel on the next session or after a config change. A nonexistent channel is silently dropped by Twitch; the join timeout marks it unavailable.
- **No replay:** the gap is the truth. The pane should show "Chat unavailable for Ns" from the gap event, not fabricate messages. This is already the replay memo's behavior (`docs/research/twitch-chat-replay.md:183-189`).

### 5.8 Persistence

Use the prior memo's layout and schema; do not invent a second format (`docs/research/twitch-chat-replay.md:197-292`). The media side is unchanged: Streamlink's `--hls-live-edge` and FFmpeg's `+program_date_time` remain the timing anchors, and the prior memo's Streamlink/FFmpeg analysis still applies (`docs/research/twitch-chat-replay.md:124-195`).

```text
recordings/{channel}/{sessionId}/
  chat/
    meta.json
    events.jsonl
    20261113T1800Z.jsonl
    20261113T1810Z.jsonl
```

Add these fields to `chat/meta.json` for IRC observability:

```json
{
  "v": 1,
  "sessionId": "20261113T180000Z_123456789",
  "channel": "example",
  "source": "irc-anonymous",
  "transport": "wss",
  "status": "recording",
  "startedAt": "2026-11-13T18:00:00.000Z",
  "endedAt": null,
  "leadInSec": 12,
  "bucketSeconds": 600,
  "lastMessageAt": "2026-11-13T18:42:11.123Z",
  "messageCount": 12345,
  "truncated": false,
  "lastError": null,
  "connectionId": "0f4f7e5e-...",
  "reconnectCount": 2,
  "droppedCount": 0,
  "parseErrorCount": 1
}
```

`source` is `irc-anonymous` or `irc-token`; `transport` is `wss` (and `tcp-tls` only if that path is built later). Do not store the token, login password, or raw `PASS` line.

`chat/events.jsonl` kinds:

```json
{"v":1,"kind":"delete","at":1763047200456,"targetId":"..."}
{"v":1,"kind":"clear_user","at":1763047201456,"targetUid":"1337","targetLogin":"ronni"}
{"v":1,"kind":"clear_all","at":1763047202456}
{"v":1,"kind":"gap","at":1763047203456,"from":1763047200000,"to":1763047260000,"reason":"irc-disconnected"}
{"v":1,"kind":"reconnect","at":1763047260000,"connectionId":"..."}
{"v":1,"kind":"dropped","at":1763047261000,"from":1763047260000,"to":1763047261000,"count":42}
{"v":1,"kind":"end","at":1763047260000}
```

Semantics are the prior memo's: `delete` applies by `targetId`; `clear_user` applies to that user's messages with `at <= event.at`; `clear_all` applies to all messages with `at <= event.at`; `gap`/`end`/`reconnect` use local wall clock; message `at` uses `tmi-sent-ts` when available. The reader filters tombstones at query time; v1 does not rewrite buckets.

Session mapping and edge cases:

- `ChatCaptureService` reconciles `RecordingLibrary.All` with `DvrStatus` every 1 s. Start a writer when a session is recording and has an active job; stop when it finalizes or the channel is removed.
- `chat/` is inside the session folder, so `RecordingAdmin.DeleteAsync` removes it (`docs/backend-ui-contract.md:193`) and `RecordingLibrary.LoadAsync` ignores it (`src/StreamKeepr/Infrastructure/Storage/RecordingLibrary.cs:91-152`). `SizeBytes` remains media-only (`docs/session-store-contract.md:39-51`).
- **Delete/finalize race:** open with `FileShare.Delete`; if the directory disappears, catch `IOException`/`UnauthorizedAccessException`, stop the writer, and log. Never fail the recording or finalize because chat I/O failed.
- **Empty session:** `SessionStore.FinalizeCoreAsync` deletes a session directory with no media (`src/StreamKeepr/Infrastructure/Storage/SessionStore.cs:335-348`). The chat writer must tolerate that deletion; do not create `chat/` before the first media or first chat event, and do not hold the directory open in a way that blocks deletion on Windows/macOS.
- **Crash:** a partial trailing line is ignored; `meta.json` is temp+rename; on restart, reopen the current bucket in append mode and seed dedupe from its tail. A crash can lose the last unflushed second; acceptable.
- **Disk cap:** `Chat:MaxBytesPerSession` (default 512 MiB) and the existing `DiskGuard`; on cap, stop capture, write `truncated`/`gap`, log once, never affect recording.
- **No database:** the repo has no database and uses temp+rename files (`docs/session-store-contract.md:53-59`). JSONL fits that posture.

### 5.9 Security

- **TLS only.** WSS default. Do not offer plaintext 6667; if TCP/TLS is added, require TLS and do not add a "skip validation" switch. `ClientWebSocket` validates certificates by default; `SslStream` must use `SslClientAuthenticationOptions.RemoteCertificateValidationCallback` that returns true only for `SslPolicyErrors.None` ([SslStream][sslstream]; [SslClientAuthenticationOptions][ssloptions]).
- **Token handling.** `PASS oauth:<token>` is sent once per connection. Never log it, never include it in exception messages, never put it in a URL or query string. Redact any raw-line debug logging (`PASS` → `PASS oauth:<redacted>`). Store it via user-secrets or `/run/secrets`, consistent with `docs/hosting-contract.md:131`.
- **Untrusted input.** Cap line/tag/param/text lengths; validate `color` as `^#[0-9A-Fa-f]{6}$`, emote IDs as `^[A-Za-z0-9_]+$`, badge set/version as `[A-Za-z0-9_-]+`; render text with `textContent`, never `innerHTML`; do not linkify in v1. The replay memo's XSS rules apply (`docs/research/twitch-chat-replay.md:400-412`).
- **Outbound injection.** StreamKeepr sends only fixed commands (`CAP`, `PASS`, `NICK`, `JOIN`, `PART`, `PONG`) plus validated channel names from `TwitchLogin` (`src/StreamKeepr/Configuration/TwitchLogin.cs:8-14`). `PONG` echoes the server's PING payload; strip CR/LF and cap length before sending. Never send user message text in v1.
- **Rate limiting.** Join queue 1/600 ms; auth attempts are naturally one per connection, but a reconnect loop must back off (20 auth/10 s). Do not send `PRIVMSG`.
- **Public endpoint.** The chat endpoint is public like playback/progress; use `Cache-Control: no-store`, validate query ranges, return 404 for unknown recordings, and never serve raw JSONL or files (`docs/hosting-contract.md:35-48,55-66`).
- **Privacy.** Chat contains display names, user IDs, and message text. Store only what the pane needs; honor `CLEARMSG`/`CLEARCHAT`; provide retention/deletion; consider access control for public instances. The Developer Agreement retention decision is an owner decision (`docs/research/twitch-chat-replay.md:116-122`).

### 5.10 Observability and health

- **`ChatStatus` singleton** with: `State` (`Disabled`, `Idle`, `Connecting`, `Registering`, `Joining`, `Live`, `Backoff`, `Unavailable`), `ConnectedAt`, `LastMessageAt`, `LastPingAt`, `ReconnectCount`, `DroppedCount`, `ParseErrorCount`, `MessageCount`, `MessagesPerSecond`, `JoinedChannels`, `LastError`, `LastErrorAt`. Raise `Changed` on transitions (throttled by the view).
- **Logs:** Information on state transitions and join/part; Warning on reconnect, auth failure, parse errors (rate-limited), drops; never log message text at Information; Debug only with redaction.
- **Health:** chat must **not** affect `/health/live`. The recording health check stays `DvrHealthCheck` (`docs/hosting-contract.md:70-89`). In v1, do not add chat to `/health/ready`; surface it in the UI/admin status. If a check is added later, tag it `chat` and report `Degraded` only, never `Unhealthy`, so a chat outage cannot stop or restart the container.
- **UI:** a small status row in the chat pane: "Chat connected", "Chat reconnecting", "Chat unavailable: <reason>", "N messages dropped". Announce transitions in the polite status region, not every message (replay memo §6.8).

### 5.11 Testing

| Layer | Project | Cases |
|---|---|---|
| Parser unit | `tests/StreamKeepr.Tests/Twitch/IrcMessageParserTests.cs`, `IrcTagParserTests.cs` | Tags, escaping (`\s`, `\:`, `\\`, `\r`, `\n`), empty/missing values, duplicate keys, unknown escapes, prefix `nick!user@host`, params, trailing, CRLF/LF, UTF-8, overlong lines, malformed lines. |
| Twitch parser | `tests/StreamKeepr.Tests/Twitch/TwitchChatParserTests.cs` | PRIVMSG with all tags; ACTION; emoji + emote code points; replies; bits; shared-chat `source-*`; CLEARMSG; CLEARCHAT user/all/timeout; ROOMSTATE; NOTICE; RECONNECT; PING; missing tags; invalid color/emote IDs. |
| State machine | `tests/StreamKeepr.Tests/Twitch/IrcChatClientTests.cs` | Connect timeout; CAP ACK/NAK; 001; 433; 464; auth NOTICE; join ack/timeout; PING/PONG; idle timeout; RECONNECT; backoff reset; `Unavailable`. |
| Join limiter | `tests/StreamKeepr.Tests/Twitch/IrcJoinQueueTests.cs` | 20/10 s; 100 cap; priority; PART/rejoin; pause/resume. |
| Backpressure | `tests/StreamKeepr.Tests/Storage/ChatLogStoreTests.cs` | Full queue drops; `dropped` event; slow writer; flush/close; no socket block. |
| Persistence | `tests/StreamKeepr.Tests/Storage/ChatLogStoreTests.cs`, `ChatReaderTests.cs` | Bucket rotation; late message; partial line; crash; delete/clear; disk cap; dedupe; session mapping; finalize/delete race. |
| Endpoint | `tests/StreamKeepr.Tests/Hosting/ChatEndpointTests.cs` | 200/400/404; `no-store`; public; `hasChat:false`; `state`; range/limit. |
| Component | `tests/StreamKeepr.Tests/Ui/ChatStatusViewTests.cs`, `ChatPaneTests.cs` | Status states; unavailable; no re-render on messages; toggle; `HasChat`. |
| JS | `tests/js/chat-pane.test.js` | Fetch/reveal; status/unavailable; gap divider; drop badge. |
| E2E | `tests/StreamKeepr.E2E/ChatJourneyTests.cs` | Mock live/finished chat; reconnect gap; chat unavailable does not affect playback; toggle persists. |

Use `FakeTimeProvider` for timeouts/backoff, `IRetryJitter`'s deterministic sample, and a fake `IIrcSocket` that yields transcript bytes. For an integration test, host an in-process Kestrel WebSocket endpoint that speaks a scripted IRC transcript; do not connect to Twitch or an external IRC network in CI. Optional manual verification: Ergo, InspIRCd, or miniircd on localhost, but those are not CI dependencies.

### 5.12 Deployment

- **Egress:** WSS 443. If TCP/TLS is added, 6697. No inbound port; chat is outbound-only. The container already exposes only 8080 (`docs/hosting-contract.md:109-134`).
- **Proxy:** `ClientWebSocketOptions.Proxy` for an explicit proxy; .NET's default `HttpClient` proxy may honor `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` environment variables, but verify in the target. Do not assume a proxy is transparent for WebSocket; test the upgrade.
- **DNS/IPv6:** use the platform resolver; do not hard-code IPs. If the host prefers IPv6, `ClientWebSocket`/`SocketsHttpHandler` handles it; verify in the container.
- **TLS trust:** the .NET runtime image includes the system CA bundle; a corporate proxy may require a custom CA. Do not disable validation; add the CA to the trust store.
- **Time sync:** `tmi-sent-ts` is Twitch server time, unaffected by host clock. Gap/end/reconnect timestamps use `TimeProvider`; keep the host on NTP for usable gap boundaries. The replay memo's sync math uses UTC instants, so a skewed host clock affects gaps, not message times.
- **Firewall:** 443 is the safest single outbound rule. 6697 may be blocked; that is one reason WSS is the recommendation.

### 5.13 Libraries

| Option | Status | Fit | Verdict |
|---|---|---|---|
| Hand-rolled IRC over WSS | Repo already hand-rolls EventSub over `ClientWebSocket` with fake sockets and `TimeProvider`. | ~400-700 lines for CAP/PASS/NICK/JOIN/PING/PONG/RECONNECT, tags, parser, state machine; full control; no new dependency. | **Recommended** |
| `TwitchLib.Client` | 4.0.2, MIT, targets `net10.0`/`net9.0`/`net8.0`/`netstandard2.0/2.1`; active. | Handles IRC, but brings its own connection/reconnect/tag model; verify anonymous login and `net10.0`; adds a dependency. | Alternative if the owner prefers a library |
| `IrcDotNet` | 0.7.0, stale. | General IRC, not Twitch-specific; no recent releases. | Reject |
| `TwitchLib.EventSub.Websockets` | Separate package. | Same EventSub authorization problem as §4.7. | Reject for v1 |

The repo rule is "no new dependency without owner approval." A small client consistent with the existing EventSub socket is the lower-risk choice. `TwitchLib.Client` is the fallback if the owner wants less protocol code; it still needs the same capture/persistence layer, so it does not remove most of the work.

### 5.14 Minimum viable IRC client checklist

- [ ] `Chat:Enabled=false` by default; enable only after the prototype.
- [ ] WSS connect with a 10 s timeout; TLS validation left on.
- [ ] `CAP LS 302`; `PASS oauth:<token>` if configured; `NICK <login>` or `justinfan<random>`; `CAP REQ :twitch.tv/tags twitch.tv/commands`; `CAP END`.
- [ ] Handle `001`, `005`, `433`, `464`, auth `NOTICE`, `CAP ACK`/`NAK`.
- [ ] Join queue 1/600 ms; max 100; join ack/`ROOMSTATE`/`366`; per-channel unavailable.
- [ ] `PING`/`PONG`; 5 min idle watchdog; `RECONNECT`; close/backoff/rejoin.
- [ ] `PRIVMSG` + tags; ACTION; emotes (code points); replies; bits; `CLEARMSG`; `CLEARCHAT`; `ROOMSTATE`.
- [ ] Bounded queue, drop counter, gap/dropped events; no socket blocking.
- [ ] JSONL buckets + `events.jsonl` + `meta.json`; dedupe by `id`; crash/partial-line tolerance.
- [ ] Status/logging; chat failure never affects recording or `/health/live`.
- [ ] Tests: parser, state machine, limiter, backpressure, persistence, endpoint.

### 5.15 What not to build

DCC, server-to-server, NickServ/ChanServ, SASL, general `MODE`/operator/voice handling, CTCP, `WHO`/`WHOIS`/`NAMES` beyond join acknowledgement, message sending/`PRIVMSG`, whispers, multi-network support, a bouncer/history service, `chathistory`, a full IRC client UI, one connection per channel, plaintext 6667, and custom cryptography. These are either irrelevant to Twitch or outside the chat-capture goal.

## 6. Comparison and decision

### 6.1 Matrix

| Option | Arbitrary channels | Auth needed | History/replay | Delivery guarantees | Latency | Capacity | Complexity | Fit |
|---|---|---|---|---|---|---|---|---|
| **IRC over WSS** | Yes, public channels; restricted rooms **UNKNOWN** | Anonymous (undocumented) or user token `chat:read` | None | No order guarantee; duplicates possible; no replay | Sub-second to ~1 s | 100 joins/user (authenticated); 20 joins/10 s; 20 auth/10 s; anonymous **UNKNOWN** | Medium | **Best for v1** |
| IRC over TCP/TLS | Same | Same | None | Same | Same | Same | Medium-high (TLS/proxy/line reader) | Fallback if 443 is unavailable |
| EventSub WebSocket | Only channels the authorized user can read (broadcaster/mod) | User token `user:read:chat` | None | At-least-once; no replay | Low | 100 joins/user; 300 subscriptions/3 connections; cost 10 | Medium | Privileged events only |
| EventSub conduit/webhook | Only with broadcaster `channel:bot` or moderator standing | App token + user token `user:read:chat`/`user:bot` + `channel:bot` | None | At-least-once; no replay | Low | ~5,000 channels; 5 conduits; 20,000 shards | High | Not for arbitrary channels |
| Polling / Helix | Yes for stream status; **no chat** | App token | No chat | At-most-once per poll | Poll interval | 100 logins/request | Low | Recording safety net only |
| VOD GQL backfill | Only if a VOD exists and chat is enabled | None (web Client-ID/hash) | Full VOD chat (unofficial) | Pull; no guarantees | Post-stream | **UNKNOWN** | Medium | Optional calibration/backfill |
| Third-party Recent Messages | Yes, public channels | None | Last ~800 messages | Pull; no guarantees | Near-real-time if polled | **UNKNOWN** | Low | Short-gap fallback only |

The EventSub rows' authorization and capacity facts are from `docs/research/twitch-chat-replay.md:81-94` and the Twitch chat auth page ([Twitch chat auth][twitch-chat-auth]); the polling row is the recording path (`docs/twitch-auth-contract.md:36-58`). The VOD GQL row is the prior memo's unofficial path (`docs/research/twitch-chat-replay.md:96-104`). The recent-messages row is its API documentation ([Recent Messages API][recent-messages]).

### 6.2 Recommendation

**Use IRC over WSS for read-only chat capture in StreamKeepr.** It is the only path that combines arbitrary public channels, no per-broadcaster authorization, no admin-session dependency, and no new package. Persist chat yourself; treat the transcript as best-effort with explicit gaps. Use EventSub only for privileged events (e.g., `stream.online`/`stream.offline` as today, or chat for channels where the owner has authorization). Keep polling as the recording safety net; chat must never influence recording.

Do not claim that IRC gives complete or ordered chat. It does not. The product promise should be "chat captured while StreamKeepr was connected, with visible gaps," not "the full chat history."

### 6.3 When IRC is the wrong choice

- **The owner can obtain broadcaster authorization for every watched channel.** EventSub conduits are cleaner, better documented, and have better capacity. Use them.
- **More than 100 concurrent channels without authorization.** IRC cannot scale past 100 per authenticated user (anonymous behavior is **UNKNOWN**). Use EventSub authorization, reduce the channel set, or accept chat only for the first 100.
- **Exact history or replay of missed messages is required.** IRC cannot provide it. Use a bouncer/history service, the unofficial VOD GQL backfill, or a third-party recent-messages service for short gaps; none is a complete solution.
- **The deployment cannot make outbound 443 or 6697.** No chat path works without egress.
- **Guaranteed delivery/ordering is a hard requirement.** Neither IRC nor EventSub chat guarantees it. Change the requirement or add an external archive.

### 6.4 Effort

Rough, including tests and docs:

| Slice | Days |
|---|---|
| WSS socket + state machine + CAP/auth/join/PING/reconnect | 1.5-2 |
| IRC/Twitch parser + message model + fixtures | 1-1.5 |
| Capture service + JSONL store + session mapping + backpressure | 1.5-2 |
| Tests/hardening (parser, state machine, limiter, persistence, endpoint) | 1.5-2 |
| **IRC slice total** | **5.5-7.5** |
| Replay UI/endpoint/sidecar from the prior memo | 3-4 |
| **Full chat feature** | **8-11** |

The prior memo estimated 6-8 days for the full slice (`docs/research/twitch-chat-replay.md:438`). The deeper protocol work and the state-machine/backpressure tests add roughly 2-3 days. Treat both as estimates; the prototype is the uncertainty reducer.

## 7. Risks, unknowns, and open questions

### 7.1 Risks and unknowns

| # | Risk / unknown | Impact | Resolution |
|---|---|---|---|
| U1 | Anonymous `justinfan` still works in Nov 2026, and at what limits | Capture may fail or be throttled | 1 h setup + 24 h soak; observe PING/RECONNECT/CLEARMSG; fallback token |
| U2 | 100-join limit semantics for anonymous identities; whether multiple connections share it | Capacity may be lower than expected; sharding may not help | Test 101 channels on one identity; assume per-user; do not use multiple identities |
| U3 | No ordering, duplicates, no replay | Gaps/dupes; transcript is not complete | Dedupe by `id`; reorder buffer; `gap`/`dropped` events; never claim completeness |
| U4 | Developer Agreement retention/privacy | Policy/legal risk for public replay | Owner decision; retention setting; honor deletions; access control option |
| U5 | IRC deprecation/atrophy | Future removal or tag changes | Abstract `IIrcChatSource`; monitor changelog; EventSub migration path |
| U6 | Twitch tag/emote/badge semantics; code-point indices | Mis-rendered emotes, wrong badges | Transcript fixtures; code-point tests; validate IDs/colors; defer images |
| U7 | Anonymous rate limits / join throttling | Disconnects, missing channels | Join queue; backoff; observe `NOTICE msg_ratelimit`; cap 100 |
| U8 | Host clock accuracy | Gap timestamps wrong | NTP; use `tmi-sent-ts` for messages; local clock only for gaps |
| U9 | WSS proxy/TLS behavior in the target container | No connection | Test egress; `ClientWebSocketOptions.Proxy`; prefer 443; custom CA if needed |
| U10 | Twitch PING interval undocumented | False idle timeout | Any inbound resets; conservative 5 min; log PING gaps |
| U11 | Anonymous access to sub-only/followers-only/verified-only rooms | Missing chat in restricted rooms | Manual test; token fallback |
| U12 | `CLEARMSG`/`CLEARCHAT` delivery to anonymous | Deleted text remains visible | Test with a moderated channel; events are best-effort; compaction later |
| U13 | Shared-chat duplicates | Same message appears in multiple channels | Dedupe by `id`; store `source-*` later if needed |
| U14 | >1,000-user channels omit JOIN/PART | No membership data | Irrelevant to `PRIVMSG`; do not depend on membership |
| U15 | Recent Messages API reliability/limits | Fallback may fail | Optional only; never primary; fail silently |
| U16 | Token scopes/expiry | Auth failure | `/oauth2/validate`; fallback anonymous; never log token |

**Top three for the owner:** U1 (anonymous durability), U2 (100-join limit and sharding), U4 (retention/ToS). U1 and U2 can invalidate the recommendation; U4 can block shipping regardless of technical success.

### 7.2 Open questions

1. **Anonymous or dedicated token for v1?** The recommendation is anonymous default with a token fallback, but the owner may prefer a documented token from day one. That changes secret management and the capture identity, not the parser.
2. **How many concurrent channels will the owner actually record?** If ≤100, one connection is enough. If >100, the design needs an explicit "chat for the first 100" policy or EventSub authorization.
3. **Retention and public exposure.** Keep chat as long as the recording, cap it, or exclude it from public replay? This is the Developer Agreement/privacy decision.
4. **Start chat at session open or first media part?** Starting at session open captures the pre-roll; starting at first media avoids a chat file for a session that produces no media. The recommendation is start on the first media/part to match the recording.
5. **Capture `USERNOTICE` in v1?** The recommendation is defer; it is not a normal chat row and adds rendering complexity.
6. **Is TCP/TLS fallback needed?** The recommendation is no for v1; add it only if the target deployment blocks 443.
7. **Export chat with the MP4?** The recommendation is folder backup only in v1; a separate export is a later feature.
8. **Is the chat pane open by default?** The prior memo recommends collapsed; this memo does not change that.

## 8. Implementation sketch

### 8.1 File map

| Area | File | Responsibility |
|---|---|---|
| Config | `src/StreamKeepr/Configuration/ChatOptions.cs`, `src/StreamKeepr/appsettings.json` | `Enabled` (default false), `TwitchUserOAuthToken`, `TwitchLogin`, `LeadInSeconds` (12), `MaxBytesPerSession` (512 MiB), `MaxChannels` (100), `JoinIntervalMs` (600), `ConnectTimeoutSeconds` (10), `RegistrationTimeoutSeconds` (15), `IdleTimeoutSeconds` (300), `ReconnectMaxSeconds` (300). Add a `Chat` section next to `Recorder` (`src/StreamKeepr/appsettings.json:15-19`). |
| IRC transport | `src/StreamKeepr/Infrastructure/Twitch/Irc/IIrcSocket.cs`, `ClientWebSocketIrcSocket.cs` | `IIrcSocket`/factory; `ClientWebSocket` connect/send/receive; 16-64 KiB frame buffer; close timeout. Mirror `ClientWebSocketEventSubSocket` (`src/StreamKeepr/Infrastructure/Twitch/ClientWebSocketEventSubSocket.cs:7-24`). |
| Line reader | `src/StreamKeepr/Infrastructure/Twitch/Irc/IrcLineReader.cs` | Byte stream -> CRLF lines; partial-line carry; 64 KiB cap; UTF-8 decode. |
| Parser | `src/StreamKeepr/Infrastructure/Twitch/Irc/IrcTagParser.cs`, `IrcMessageParser.cs`, `IrcMessage.cs` | Pure tag/prefix/command/params parser; bounds; malformed -> null. |
| Twitch mapping | `src/StreamKeepr/Infrastructure/Twitch/Irc/TwitchChatParser.cs`, `TwitchChatEvent.cs` | Tags/commands -> `TwitchChatMessage`/events; ACTION; emotes; replies; clears. |
| Client | `src/StreamKeepr/Infrastructure/Twitch/Irc/IrcChatClient.cs`, `IrcConnectionState.cs`, `IrcJoinQueue.cs` | State machine; CAP/auth/join/PING/PONG/RECONNECT; backoff; join limiter; status events. |
| Capture | `src/StreamKeepr/Infrastructure/Twitch/ChatCaptureService.cs` | `BackgroundService`; reconcile `RecordingLibrary`/`DvrStatus`; start/stop per session; route events; write gaps. |
| Store | `src/StreamKeepr/Infrastructure/Storage/ChatLogStore.cs` | Bucket writer/reader; `events.jsonl`; `meta.json`; atomic meta; disk cap; partial-line handling. |
| Reader | `src/StreamKeepr/Infrastructure/Storage/ChatReader.cs` | Range query; bucket selection; dedupe; tombstones; ordering; limit/cursor. |
| Timeline | `src/StreamKeepr/Infrastructure/Storage/ChatTimeline.cs` | Part PDT/duration/media-start anchors; media<->wall mapping; gaps. |
| Endpoint | `src/StreamKeepr/Hosting/ChatEndpoint.cs` | `GET /api/recordings/{id}/chat`; validation; `no-store`; 400/404. |
| Status | `src/StreamKeepr/Ui/ChatStatusView.cs` | Scoped view over `ChatStatus`; signed-in or public? The pane needs status; keep it public but expose no secrets. |
| UI shell | `src/StreamKeepr/Components/Features/Chat/ChatPane.razor` | Skeleton, header/toggle, log container, status, sync footer; `ShouldRender() => false`; JS owns the log. |
| UI JS | `src/StreamKeepr/Components/Features/Chat/ChatPane.razor.js` | Fetch/poll, reveal queue, DOM, auto-scroll, nudge, `sk:playhead` listener, status. |
| Player seam | `src/StreamKeepr/Components/Features/Player/VideoPlayer.razor.js` | Throttled `sk:playhead`; ignore keys inside `[data-sk-chat]` (`docs/player-contract.md:107-111`). |
| Wiring | `src/StreamKeepr/Program.cs` | `ChatOptions` validation, DI, `MapChatEndpoint`, hosted service. Follow `Program.cs:44-66,81-103,143-162`. |
| Tests | `tests/StreamKeepr.Tests/Twitch/Irc*.cs`, `TwitchChatParserTests.cs`, `tests/StreamKeepr.Tests/Storage/Chat*.cs`, `tests/StreamKeepr.Tests/Hosting/ChatEndpointTests.cs`, `tests/StreamKeepr.Tests/Ui/Chat*.cs`, `tests/js/chat-pane.test.js`, `tests/StreamKeepr.E2E/ChatJourneyTests.cs` | See §8.4. |

### 8.2 Interfaces and DI lifetimes

```csharp
public interface IIrcSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

public interface IIrcSocketFactory
{
    IIrcSocket Create();
}

public interface IIrcChatSource : IAsyncDisposable
{
    ChatConnectionState State { get; }
    event Action<ChatConnectionState>? StateChanged;
    IAsyncEnumerable<TwitchChatEvent> ReadAllAsync(CancellationToken ct);
    ValueTask JoinAsync(string channel, CancellationToken ct);
    ValueTask PartAsync(string channel, CancellationToken ct);
}
```

| Service | Lifetime | Why |
|---|---|---|
| `ChatOptions` | Singleton options | Bound and validated at startup. |
| `IIrcSocketFactory` | Singleton | Stateless factory; production `ClientWebSocketIrcSocketFactory`, test fake. |
| `IIrcChatSource` / `IrcChatClient` | Singleton | One multiplexed connection. |
| `ChatCaptureService` | Singleton + `AddHostedService` | Owns session reconciliation and writers. |
| `ChatLogStore` | Singleton | Owns per-session writer dictionary; one writer per session. |
| `ChatReader` | Singleton | Stateless range queries over files. |
| `ChatTimeline` | Singleton | Caches part timing per session. |
| `ChatStatus` | Singleton | Runtime status for UI/health. |
| `ChatStatusView` | Scoped | Per-circuit view; subscribes to `Changed`; throttles. |

Do not add a second `HttpClient` for chat; WSS uses `ClientWebSocket`. If TCP/TLS is added later, `IIrcSocket` hides it and the state machine/parser do not change.

### 8.3 Pseudocode

```csharp
public sealed class IrcChatClient(
    IIrcSocketFactory sockets,
    IrcLineReader lines,
    IrcMessageParser parser,
    TwitchChatParser twitch,
    IrcJoinQueue joins,
    ChatStatus status,
    IOptions<ChatOptions> options,
    IRetryJitter jitter,
    TimeProvider clock,
    ILogger<IrcChatClient> logger) : IIrcChatSource
{
    private static readonly Uri ChatEndpoint = new("wss://irc-ws.chat.twitch.tv:443");

    public async IAsyncEnumerable<TwitchChatEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            await using IIrcSocket socket = sockets.Create();
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(options.Value.ConnectTimeoutSeconds));
                await socket.ConnectAsync(ChatEndpoint, connect.Token);

                await SendAsync(socket, "CAP LS 302", ct);
                if (options.Value.TwitchUserOAuthToken is { Length: > 0 } token)
                    await SendAsync(socket, $"PASS oauth:{token}", ct);
                await SendAsync(socket, $"NICK {Nick()}", ct);
                await SendAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands", ct);
                await SendAsync(socket, "CAP END", ct);

                await foreach (IrcLine line in lines.ReadAllAsync(socket, ct))
                {
                    IrcMessage? message = parser.Parse(line);
                    if (message is null) { status.ParseError(); continue; }

                    switch (message.Command)
                    {
                        case "001":
                            State = ChatConnectionState.Joining;
                            // One JOIN per ~600 ms; await JOIN/ROOMSTATE/366 or 10 s; cap 100.
                            await joins.JoinAllAsync(socket, ct);
                            break;
                        case "PING":
                            await SendAsync(socket, $"PONG :{Sanitize(message.Trailing)}", ct);
                            status.Ping();
                            break;
                        case "RECONNECT":
                            throw new IrcReconnectException();
                        default:
                            foreach (TwitchChatEvent evt in twitch.Parse(message))
                                yield return evt;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "IRC session failed; reconnecting.");
                status.Backoff();
                await Task.Delay(jitter.Apply(backoff), clock, ct);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2,
                    TimeSpan.FromSeconds(options.Value.ReconnectMaxSeconds).Ticks));
            }
        }
    }
}
```

This is a sketch, not production code. The real client needs a single-writer send queue (because `ClientWebSocket` supports only one send and one receive at a time; see [ClientWebSocket][clientwebsocket]), a registration timeout, CAP ACK/NAK handling, join acknowledgement tracking, and gap emission. Do not `await` disk writes inside the `await foreach`.

### 8.4 Test matrix

| Layer | Cases |
|---|---|
| Parser unit | Tags/escaping/prefix/params/trailing; multiple lines per frame; partial line; CRLF and LF; UTF-8; overlong; malformed; duplicate tags; unknown tags. |
| Twitch parser | PRIVMSG all tags; ACTION; emoji + emote code points; replies; bits; shared chat; CLEARMSG; CLEARCHAT user/all/timeout; ROOMSTATE; NOTICE; RECONNECT; PING; missing/invalid tags; invalid color/emote/badge. |
| State machine | Connect/TLS/registration timeouts; CAP ACK/NAK; 001; 433/464; auth NOTICE; join ack/timeout; PING/PONG; idle; RECONNECT; backoff/reset; `Unavailable`. |
| Join limiter | 20/10 s; 100 cap; priority; PART/rejoin; pause/resume; multiple channels per `JOIN`. |
| Backpressure | Full queue drops; `dropped` event; slow writer; flush/close; socket read loop never blocks. |
| Persistence | Bucket rotation; late message; partial trailing line; crash/reopen; delete/clear; disk cap; dedupe; session mapping; finalize/delete race. |
| Endpoint | 200/400/404; `no-store`; public; `hasChat:false`; `state`; `limit`/range; malformed query. |
| Component/JS | `ChatPane` skeleton; status/unavailable; no re-render on messages; toggle; `HasChat`; gap divider; drop badge; `sk:playhead`. |
| E2E | Mock live/finished chat; reconnect gap; chat unavailable does not affect playback; toggle persists across reload; axe/keyboard. |

## 9. Sources

All web sources fetched **2026-11-13** unless noted. Repository citations are from the working tree on 2026-11-13 (HEAD `2a8b2df`).

### 9.1 IRC standards and history

- Oikarinen, "IRC History by Jarkko Oikarinen": http://www.irc.org/history_docs/jarkko.html (the irc.org host timed out during a direct fetch; the same text is mirrored at https://www.mirc.com/history.html)
- RFC 1459, Internet Relay Chat Protocol: https://www.rfc-editor.org/rfc/rfc1459
- RFC 2810, Internet Relay Chat: Architecture: https://www.rfc-editor.org/rfc/rfc2810
- RFC 2811, Internet Relay Chat: Channel Management: https://www.rfc-editor.org/rfc/rfc2811
- RFC 2812, Internet Relay Chat: Client Protocol: https://www.rfc-editor.org/rfc/rfc2812
- RFC 2813, Internet Relay Chat: Server Protocol: https://www.rfc-editor.org/rfc/rfc2813
- RFC 7194, Default Port for IRC via TLS/SSL: https://www.rfc-editor.org/rfc/rfc7194
- Modern IRC Client Protocol: https://modern.ircdocs.horse/
- Libera.Chat registration/services guide: https://libera.chat/guides/registration
- ZNC bouncer: https://wiki.znc.in/ZNC

### 9.2 IRCv3

- Specifications index: https://ircv3.net/irc/
- Message tags: https://ircv3.net/specs/extensions/message-tags
- Client capability negotiation: https://ircv3.net/specs/extensions/capability-negotiation
- Server-time: https://ircv3.net/specs/extensions/server-time
- Message IDs: https://ircv3.net/specs/extensions/message-ids
- Batch: https://ircv3.net/specs/extensions/batch
- Labeled response: https://ircv3.net/specs/extensions/labeled-response
- Echo message: https://ircv3.net/specs/extensions/echo-message
- Chat history (work in progress): https://ircv3.net/specs/extensions/chathistory
- Read marker: https://ircv3.net/specs/extensions/read-marker
- Monitor: https://ircv3.net/specs/extensions/monitor
- Extended join: https://ircv3.net/specs/extensions/extended-join
- Away notify: https://ircv3.net/specs/extensions/away-notify
- Account notify: https://ircv3.net/specs/extensions/account-notify
- Account tag: https://ircv3.net/specs/extensions/account-tag
- SASL 3.1: https://ircv3.net/specs/extensions/sasl-3.1

### 9.3 Twitch

- IRC concepts: https://dev.twitch.tv/docs/chat/irc/
- Chat & Chatbots overview / rate limits: https://dev.twitch.tv/docs/chat/
- Authenticating and setting up EventSub: https://dev.twitch.tv/docs/chat/authenticating/
- Migrating from IRC: https://dev.twitch.tv/docs/chat/irc-migration/
- EventSub chat reference: https://dev.twitch.tv/docs/eventsub/reference/chat/
- Managing EventSub subscriptions: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/
- Chat API reference (badges/emotes/template): https://dev.twitch.tv/docs/api/reference/chat/
- Videos API reference: https://dev.twitch.tv/docs/api/reference/videos/
- Developer Agreement: https://www.twitch.tv/p/en/legal/developer-agreement/
- Docs changelog: https://dev.twitch.tv/docs/change-log/
- Validating tokens: https://dev.twitch.tv/docs/authentication/validate-tokens/
- 2025 non-secure WebSocket decommission: https://discuss.dev.twitch.com/t/decommission-of-non-secure-websocket-connections-to-twitch-irc-servers/64142
- 2024 concurrent join limits: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997
- 2025 justinfan reply: https://discuss.dev.twitch.com/t/can-i-use-wss-irc-ws-chat-twitch-tv-with-justifan/63732
- 2024 "never officially documented" reply: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997/17

### 9.4 .NET

- `ClientWebSocket`: https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket?view=net-10.0
- `ClientWebSocketOptions.Proxy`: https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.proxy?view=net-10.0
- `SslStream`: https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0
- `SslClientAuthenticationOptions`: https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions?view=net-10.0
- System.IO.Pipelines: https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines
- System.Threading.Channels: https://learn.microsoft.com/en-us/dotnet/core/extensions/channels
- `TimeProvider`: https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider?view=net-10.0

### 9.5 Libraries and implementations

- Chatterino source, anonymous username: https://github.com/Chatterino/chatterino2/blob/master/src/providers/twitch/TwitchCommon.hpp
- Chatterino source, anonymous connection: https://github.com/Chatterino/chatterino2/blob/master/src/providers/twitch/TwitchIrcServer.cpp
- Streamlink HLS live edge: https://github.com/streamlink/streamlink/blob/8.6.3/src/streamlink/stream/hls/hls.py#L507-L508
- Streamlink Twitch plugin docs: https://streamlink.github.io/cli/plugins/twitch.html
- TwitchLib.Client NuGet: https://www.nuget.org/packages/TwitchLib.Client
- TwitchLib repository: https://github.com/TwitchLib/TwitchLib
- IrcDotNet NuGet: https://www.nuget.org/packages/IrcDotNet
- Recent Messages API: https://recent-messages.robotty.de/api
- TwitchDownloader GQL source: https://github.com/lay295/TwitchDownloader/blob/master/TwitchDownloaderCore/ChatDownloader.cs
- chat-downloader GQL source: https://github.com/xenova/chat-downloader/blob/master/chat_downloader/sites/twitch.py

### 9.6 Repository citations

- `src/StreamKeepr/Infrastructure/Twitch/ClientWebSocketEventSubSocket.cs:19` — 3 s close timeout; `:23-24` connect; `:26-68` receive/frame handling; `:70-94` dispose.
- `src/StreamKeepr/Infrastructure/Twitch/EventSubClient.cs:25-35` — base URI, backoff, keepalive/connect timeouts; `:46-131` run loop; `:134-139` jittered backoff; `:155-170` connect timeout; `:175-244` read/keepalive/handoff.
- `src/StreamKeepr/Infrastructure/Twitch/TwitchTokenProvider.cs:21-38` — client-credentials token; `:50-53` early refresh; `:61-66` invalidate.
- `src/StreamKeepr/Infrastructure/Storage/SessionStore.cs:27-30` — manifest/index/thumbnail names; `:47-56` per-session locks; `:62-94` open/reopen; `:149-163` finalize; `:335-348` empty-session delete; `:418-423` atomic write.
- `src/StreamKeepr/Infrastructure/Storage/RecordingLibrary.cs:36-37` — `Changed`; `:91-152` startup scan/load; `:154-169` sort.
- `src/StreamKeepr/Configuration/RecorderOptions.cs:17-18` — Streamlink user token; `:20,22` segment seconds/live edge.
- `src/StreamKeepr/Configuration/TwitchLogin.cs:8-14` — authoritative login normalization/validation.
- `src/StreamKeepr/appsettings.json:9-19` — Twitch/Recorder sections; no Chat section.
- `src/StreamKeepr/Program.cs:44-66` — options validation; `:81-103` singletons; `:123-135` EventSub; `:143-162` hosted services.
- `docs/session-store-contract.md:9,24-35,53-59` — session folder, manifest, atomic writes; `:39-51` derived summary fields (SizeBytes media-only).
- `docs/runtime-contract.md:9-20` — component lifetimes; `:22-30` event queue; `:90-101` startup; `:103-109` shutdown.
- `docs/hosting-contract.md:35-48` — endpoint map; `:55-66` media allowlist; `:109-134` container; `:136-141` shutdown.
- `docs/twitch-auth-contract.md:19` — admin token only for EventSub; `:36-58` poller/push coverage; `:66-90` EventSub WebSocket mode.
- `docs/backend-ui-contract.md:23-28` — `RecordingView`; `:193` delete removes the session folder.
- `docs/frontend-foundation-contract.md:38-52` — shell layout; `:71-76` `sk-ui` cookie; `:142` JS cadence rule.
- `docs/player-contract.md:17-19` — JS-owned player; `:54-57` live edge; `:65-80` control order; `:107-111` keyboard exclusions.
- `docs/testing-contract.md:13-20` — test layers; `:49-56` seams.
- `docs/research/twitch-chat-replay.md:11` — prior recommendation; `:81-94` EventSub chat rejection; `:124-195` timing/sync; `:197-292` persistence/schema; `:400-412` rendering/XSS; `:438` effort.

[irc-history]: http://www.irc.org/history_docs/jarkko.html
[rfc1459]: https://www.rfc-editor.org/rfc/rfc1459
[rfc2810]: https://www.rfc-editor.org/rfc/rfc2810
[rfc2811]: https://www.rfc-editor.org/rfc/rfc2811
[rfc2812]: https://www.rfc-editor.org/rfc/rfc2812
[rfc2813]: https://www.rfc-editor.org/rfc/rfc2813
[rfc7194]: https://www.rfc-editor.org/rfc/rfc7194
[modern-irc]: https://modern.ircdocs.horse/
[libera-services]: https://libera.chat/guides/registration
[znc]: https://wiki.znc.in/ZNC
[ircv3-specs]: https://ircv3.net/irc/
[ircv3-tags]: https://ircv3.net/specs/extensions/message-tags
[ircv3-cap]: https://ircv3.net/specs/extensions/capability-negotiation
[ircv3-servertime]: https://ircv3.net/specs/extensions/server-time
[ircv3-msgid]: https://ircv3.net/specs/extensions/message-ids
[ircv3-batch]: https://ircv3.net/specs/extensions/batch
[ircv3-chathistory]: https://ircv3.net/specs/extensions/chathistory
[ircv3-sasl]: https://ircv3.net/specs/extensions/sasl-3.1
[twitch-irc]: https://dev.twitch.tv/docs/chat/irc/
[twitch-chat]: https://dev.twitch.tv/docs/chat/
[twitch-chat-auth]: https://dev.twitch.tv/docs/chat/authenticating/
[twitch-migration]: https://dev.twitch.tv/docs/chat/irc-migration/
[twitch-changelog]: https://dev.twitch.tv/docs/change-log/
[twitch-validate]: https://dev.twitch.tv/docs/authentication/validate-tokens/
[twitch-ws-decommission]: https://discuss.dev.twitch.com/t/decommission-of-non-secure-websocket-connections-to-twitch-irc-servers/64142
[join-limits]: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997
[justinfan-2025]: https://discuss.dev.twitch.com/t/can-i-use-wss-irc-ws-chat-twitch-tv-with-justifan/63732
[justinfan-undoc]: https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997/17
[chatterino]: https://github.com/Chatterino/chatterino2/blob/master/src/providers/twitch/TwitchCommon.hpp
[chatterino-irc]: https://github.com/Chatterino/chatterino2/blob/master/src/providers/twitch/TwitchIrcServer.cpp
[recent-messages]: https://recent-messages.robotty.de/api
[clientwebsocket]: https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket?view=net-10.0
[sslstream]: https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0
[ssloptions]: https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions?view=net-10.0
[pipelines]: https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines
[channels]: https://learn.microsoft.com/en-us/dotnet/core/extensions/channels
[tmi-js]: https://tmijs.com/
[tmi-js-source]: https://github.com/tmijs/tmi.js/blob/main/lib/ClientBase.js
[twitch-irc-crate]: https://docs.rs/twitch-irc/latest/twitch_irc/
[twitch-irc-login]: https://github.com/robotty/twitch-irc-rs/blob/main/src/login.rs
[twitch-irc-handshake]: https://github.com/robotty/twitch-irc-rs/blob/main/src/connection/event_loop.rs
[recent-messages2]: https://github.com/robotty/recent-messages2/blob/main/src/irc_listener.rs
[chat-downloader-irc]: https://github.com/xenova/chat-downloader/blob/master/chat_downloader/sites/twitch.py
[twitchlib-1086]: https://github.com/TwitchLib/TwitchLib/issues/1086
