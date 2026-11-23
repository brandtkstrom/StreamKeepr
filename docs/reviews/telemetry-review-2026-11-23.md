# Telemetry review — 2026-11-23

Status: advisory. Findings 1, 2 and 9 have been fixed, each with a regression test (see their **Fixed** notes). Grafana-side fixes are listed under "Observability stack". The rest are open.

## Scope and data examined

The live Compose deployment (`compose.yaml` + `compose.observability.yaml`, app on `:5090`, image built from `main` at `2c59c88`), queried directly:

| Store | Window | Volume |
| --- | --- | --- |
| Prometheus | 18:29–18:50 UTC (stack start to review) | 3,642 active series; 5 `streamkeepr_*` metrics present |
| Tempo | last 2 h | 59 traces; span names: `streamkeepr.twitch.poll` and bare HTTP methods |
| Loki | 18:29:43–18:47:30 UTC | 878 events: 768 Debug, 38 Information, 72 Warning, 0 Error |

During the window, two channels were added from the UI and were both recording. No part finished, so no export ran and no chat drop happened. The recorder, export, chat, and EventSub-reconnect counters therefore had no series yet. That matches `observability/README.md`, but see finding 9.

The window is short (about 20 minutes of logs). Steady-state findings (rates, noise) are reliable. Anything that depends on rare events (stalls, force kills, Helix failures) went unobserved and is assessed from code only.

## Findings

Ranked by impact. Each one cites the evidence from the captured data.

### 1. Request context leaks into long-lived channel work (bug)

Every log line from a channel added in the UI carries the adding Blazor request's context, including recorder progress hours later:

```
Recorder {Job} progress …  Channel=alveussanctuary  RequestPath=/_blazor
  ConnectionId=0HNQ9HUVOEIDQ  RequestId=0HNQ9HUVOEIDQ:00000001  TransportConnectionId=PbnkL1Kw…
```

The same request context appears on `SessionStore`, `ThumbnailService`'s caller, `TwitchTokenProvider` ("Acquired a Twitch app token (initial)"), and the DVR transition logs.

**Cause:** `DvrSupervisor.StartChannel` (`Infrastructure/Runtime/DvrSupervisor.cs:315`) starts the channel loop with `Task.Run` from inside the `AddAsync` call that the Blazor circuit makes. `ExecutionContext` flows, and with it the ASP.NET Core logging scope, SignalR's scope, and `Activity.Current`. `ChannelCoordinator.cs:339` then inherits it for every part.

**Impact:** filtering logs by request or connection returns hours of recorder output. `/_blazor` is untraced today, so no trace is polluted yet. If any traced path ever adds a channel (an API or a test), every recorder and Helix span for that channel's lifetime would join that one trace. Channels started at boot are unaffected, which makes the bug look intermittent.

**Fix:** start the loop under `ExecutionContext.SuppressFlow()` (or clear `Activity.Current` and start it from a clean context). Audit other request-triggered starters the same way, such as export jobs and chat joins. A regression test can assert that a channel added inside a logging scope does not log with that scope.

**Fixed:** `StartChannel` now starts the loop under `ExecutionContext.SuppressFlow()`. Test: `DvrSupervisorTests.A_channel_added_inside_a_request_does_not_carry_its_log_scope_or_activity`. Export jobs and chat joins have not been audited yet.

### 2. False "keepalive timeout" Warning on a voluntary EventSub restart (bug)

```
18:30:55.404880 WARN  EventSub WebSocket restart: keepalive timeout.
18:30:55.405019 INFO  Push coverage changed Connected -> EventSubReconnecting
18:30:55.405244 INFO  EventSub WebSocket session closed: watched channels changed.
```

This was a channel add, not a keepalive failure. In `RunSessionAsync` (`Infrastructure/Twitch/EventSubClient.cs:240–286`), cancelling `sessionCts` completes the receive task (cancelled) and the watchdog `Task.Delay` (cancelled through `timeoutCts`). When `WhenAny` returns the watchdog, the code reaches `throw new EventSubRestartException("keepalive timeout")` without checking `ct`. The outer loop logs the Warning and then also logs the real reason.

**Impact:** a phantom Warning on each channel add or remove that loses the race, and it is the only non-health Warning in the window. Reconnect counting is correct, because the `finally` block checks `sessionCts`.

**Fix:** `ct.ThrowIfCancellationRequested()` before classifying a timeout, plus a test with a fake clock that cancels during a wait.

**Fixed:** as proposed. Test: `EventSubClientTests.Channel_change_is_not_reported_as_a_keepalive_timeout_when_the_watchdog_completes_first`. It uses a fake socket that finishes a cancelled receive late, so the watchdog reliably wins the race.

### 3. Disk-degraded health check floods Warnings, but Degraded is invisible in metrics

71 of the 72 Warnings are `DefaultHealthCheckService`, `Health check disk with status Degraded … '15 GB free'`, logged on every `/health/ready` probe (about 4 per minute, about 5,700 per day). `DiskSpaceHealthCheck` degrades below `2 × MinFreeGb` (20 GB).

Meanwhile `/health/ready` still answers 200 when Degraded, so `httpcheck_status{http_status_class="2xx"}` reads 1 and the dashboard's readiness tile shows healthy. The one place the degraded state is visible is the log flood, which also drowns out the "Warnings and errors" panel.

**Fix:** publish health results as a gauge, for example `streamkeepr.health.status{check}` (0/1/2) via `IHealthCheckPublisher` or the existing check results. Then raise `Microsoft.Extensions.Diagnostics.HealthChecks` to `Error` in the Serilog overrides.

### 4. Log → trace links point at traces that were never recorded

Health-probe log lines carry `trace_id`/`span_id` (for example `cbe2bfe3…`). `/health/*` is filtered out of tracing by `TelemetryPaths.ShouldTrace`, so the activity exists but is not recorded, and Grafana's "Trace" link on those lines resolves to nothing in Tempo. The same will happen for `/media`, `/_blazor`, and `/signin-twitch` lines.

**Fix:** a small Serilog enricher (or a filter in `StreamKeeprLogging`) that drops `TraceId`/`SpanId` when `Activity.Current?.Recorded` is false.

### 5. OTLP exporter traffic is counted as app HTTP-client traffic

`http_client_request_duration_seconds_count{server_address="otel-collector"}`: 68 calls in the window, more than all Twitch calls combined (34 Helix GETs, 4 POSTs). The dashboard's "Twitch API calls (HTTP client)" panel includes it.

**Fix:** filter `server_address!="otel-collector"` in the panel, or drop it with a metrics view. Traces are already suppressed by the SDK.

### 6. Static assets are traced and metered under fingerprinted routes

10 of the 24 non-poll traces are static files (`GET _framework/blazor.web.ax6tuj8tun.js`, `GET Components/Layout/AppShell.zrxpb99uij.razor.js`, …), and the same fingerprinted names are `http_route` labels on `http_server_request_duration_seconds`. Every build mints new route series, and "Recent traces" is mostly asset fetches.

`RequestLogPolicy` already recognises static assets by extension, but `TelemetryPaths` does not.

**Fix:** have `TelemetryPaths` exclude static assets too, sharing one extension list with `RequestLogPolicy` (DRY).

### 7. Blazor component metrics are almost half of all series

`aspnetcore_components_update_parameters_duration_seconds_bucket` has 1,691 series: 89 component types × 19 buckets. All `aspnetcore_components_*` together are about 50% of the 3,642 series. This grows with every new component and is rarely queried.

**Fix:** a metrics view that drops `aspnetcore.components.type` from that histogram, or drops the instrument entirely. Keep `circuit_active`/`circuit_connected`, which are useful.

### 8. Background Twitch calls show up as context-free root traces

The roots named just `POST` and `GET` are HttpClient spans with no parent: `POST /helix/eventsub/subscriptions` (6), `GET wss://eventsub…` (2), `GET /helix/users` (2), `POST id.twitch.tv/oauth2/token` (2), and `GET wss://irc-ws…` (1). Nothing says which operation (channel add, EventSub session setup, token refresh, chat connect) they belong to. Only the poller has a parent span.

**Fix:** see "Traces" under recommendations.

### 9. Lazily created counters lose their first increment

`streamkeepr_recorder_parts_total`, `…_stalls_total`, `…_force_kills_total`, `…_eventsub_reconnects_total`, `…_export_jobs_total`, and `…_chat_dropped_total` have no series until their first event. Prometheus `increase()` cannot see the first 0→1 step of a new series, so the first stall or force-kill after each restart reads as 0 on the dashboard.

**Fix:** initialize each counter with `Add(0)` for its known label values at startup (exit kinds `exited|stopped|killed`, export outcomes, transport).

**Fixed:** `StreamKeeprTelemetry.InitializeCounters` records a 0 for every known series (each exit kind, each export outcome, the configured EventSub transport, and the unlabeled counters). `Program.cs` calls it from `ApplicationStarted`, because the meter provider is not listening while hosted services are being constructed. Export outcomes are now shared constants that `ExportService` uses. Tests: `StreamKeeprTelemetryTests.Initialize_counters_*`, plus `TelemetryHostTests.Enabled_host_exports_zeroed_event_counters_once_started`, which checks the zeros reach the OTel exporter and fails without the startup call. The part duration and size histograms cannot be zeroed this way, because a 0 sample would distort them, so their first part per exit kind after a start is still not counted by `rate()`.

### 10. Steady first-chance exceptions with no matching logs

`dotnet_exceptions_total`: about 1.8 `IOException` per minute and about 2 `OperationCanceledException` per minute, flat across the window, with no Warning or Error logs. The source is unattributed: the 30 s image `HEALTHCHECK` (`curl`), the IRC or EventSub sockets, and pipe reads are candidates. Probably benign, but nothing currently tells you which.

**Next step:** a one-off `dotnet-trace`/`dotnet-counters` session, or a temporary `FirstChanceException` debug log, to attribute them.

### 11. Smaller items

- **`service.version` is `1.0.0` for every build**, so traces and `target_info` cannot tell deploys apart. Stamp `InformationalVersion` with the commit SHA through a Docker build arg (`.git` is excluded from the build context).
- **ffmpeg warnings are invisible at the shipped level.** 336 `[ffmpeg] … Packet duration: -60 / dts … out of range` lines arrived in 17 minutes, all at Debug. Production runs at Information, so a muxer complaining continuously leaves no signal. See the recorder metrics below.
- **`_r` metadata.** Serilog's `@r` (rendered format specifiers) passes through the collector as a `_r` field. Remove it in `otel-collector.yaml` like `@t`/`@l`.
- **Part-duration buckets stop at 4 h** (`DurationBuckets` max 14,400 s). Healthy parts of long streams all land in `+Inf`, so p50/p95 for them is meaningless. Add 28,800 and 43,200.
- **An admin Twitch login is logged at Information** on every token refresh (`AdminTokenStore`) and now ships to Loki. Consider logging that the refresh happened without the login, in line with the memo's "channel logins: no by default".
- **The stack has no self-monitoring.** The collector's own metrics (refused by `memory_limiter`, exporter failures, dropped spans) and Loki/Tempo ingestion are not scraped, so telemetry loss is silent.

## Observability stack (`observability/`)

Reviewed after the findings above. The Grafana changes were checked by running every panel query through a throwaway Grafana 13.3.1, Prometheus, Loki, and Tempo started on copies of the stack's data volumes. All queries run without errors. The provisioned data sources and dashboard load without errors.

### Fixed in Grafana

| Problem | Fix |
| --- | --- |
| The liveness and readiness tiles used a range query reduced with `lastNotNull`, so they kept showing UP after probes stopped (verified: the stopped stack still read UP). | Instant query with `or vector(-1)`, shown as `NO DATA`. |
| Prometheus data source had no `timeInterval`. Metrics arrive every 30 s, but Grafana assumed 15 s, so `$__rate_interval` could hold one sample and rate panels had gaps. | `timeInterval: 30s`. |
| "Twitch API calls" included the app's own OTLP exports, which were the biggest series (finding 5). | Renamed "Outbound HTTP calls"; `server_address!="otel-collector"`. |
| Route rate and latency were dominated by fingerprinted static assets (finding 6). | Routes matching `RequestLogPolicy`'s static extensions and `_framework`/`_content` are excluded. |
| A Degraded health check was invisible: readiness stays 200 (finding 3). | "Degraded or unhealthy health checks" panel from the `HealthCheckEnd` log events. |
| "Warnings and errors" grouped only by level, so the health-check flood had no visible source. | Grouped by level and `SourceContext`, stacked. |
| The free-space and poll-age tiles had no thresholds. | Free space: orange below 20 GiB, red below 10 GiB (`MinFreeGb` defaults). Poll age: orange past 330 s, red past 600 s. |
| Collected data with no panel: circuits, SignalR and Kestrel connections, CPU, exceptions. | "Connections", "CPU", and "Exceptions by type" panels. |
| No per-channel view, because channel logins are kept out of metric labels by design. | Log panels for channel state changes and recorder parts (rendered with `line_format`), and for EventSub and chat connection events. |
| No way to see traces that failed. | "Errored traces" table (`status=error`). |
| Restarts and deploys were not visible on any graph. | "App start and stop" annotations from `HostLifecycleLog`. |

### Confirmed, not fixable in Grafana

- **Finding 9 is real.** Two parts finished when the stack stopped and `streamkeepr_recorder_parts_total` has samples, but "Recorder events (per hour)" and the duration and size percentiles show nothing, because `increase()` ignores a new series' first value. Now fixed in the app (see finding 9). The duration and size percentiles still miss the first part per exit kind after a start, so the "Recorder parts" log panel remains the complete record.
- **Log bodies are message templates**, such as `Recorder {Job} started part {Part} for {Channel}`. `CompactJsonFormatter` writes no rendered message, so the generic Logs panel shows templates, with values only in the expanded details. Fix: switch the file sink to `RenderedCompactJsonFormatter` (adds `@m`) and move `@m` to the body in `otel-collector.yaml`, keeping `@mt` as an attribute. That is an app and collector change, not Grafana.

### Remaining stack issues (not changed)

- **No self-monitoring.** Prometheus scrapes only itself. The collector's internal metrics (refused by `memory_limiter`, exporter failures, dropped data) and Loki/Tempo ingestion and retention metrics are not collected, so telemetry loss is silent. Add scrape jobs for `loki:3100/metrics` and `tempo:3200/metrics`, and expose the collector's internal metrics (`service.telemetry.metrics`) on the compose network.
- **`_r` field.** The collector does not remove Serilog's `@r` (finding 11).
- **No alert rules** (P3 above).
- **The deprecated `batch` processor.** Recent collector releases favor exporter-side batching. It works today; revisit it when the collector image is bumped.

## Coverage map

| Component | Traces | Metrics | Logs | Main gap |
| --- | --- | --- | --- | --- |
| Twitch poller | `streamkeepr.twitch.poll` + Helix child | poll age, push coverage | Debug summary | no poll-outcome counter; no Helix rate-limit budget |
| EventSub (WS/conduit) | orphan HTTP spans only | connected, reconnects | good, plus finding 2 | no session-setup span; no notification counter or latency |
| Token provider / admin auth | orphan HTTP span | — | Info | no refresh-failure counter |
| DVR supervisor / coordinator | — | channels by state | transition log per channel | no transition counter; no per-action span |
| Recorder job | — | parts, duration, size, stalls, kills (on exit only) | progress at Debug | nothing live: no bytes rate, last-segment age, or time-to-first-media |
| Session store / recovery / library | — | — | Info with elapsed time | no write-failure counter; startup timings only in logs |
| Thumbnails | — | — | Info | no failure counter |
| Export | — | jobs by outcome | Info | no duration or size; no span |
| Chat capture (IRC) | orphan WSS span | dropped only | state changes | no messages-in, reconnects, bytes written, or cap hits |
| Emotes (BTTV) | HttpClient spans | HttpClient metrics | sparse | no cache hit/miss |
| Media / live index | excluded | excluded | Verbose | no playback-serving signal at all |
| Blazor UI | excluded | circuits plus noisy component metrics | — | user actions untraced |
| Health | — | external `httpcheck` only | Warning flood | finding 3 |

## Recommended additions

Constraints carried over from `docs/research/opentelemetry.md`: no channel login as a metric label, low cardinality, instruments are no-ops when telemetry is off, and nothing in callbacks does I/O.

### P1 — the recorder is the product, and it is blind while running

1. `streamkeepr.recorder.bytes` (Counter, By). Add the deltas `byteTracker` already computes (#246). `rate()` gives total ingest throughput, and a flat line while `dvr_channels{recording}>0` is the earliest stall signal, well ahead of the watchdog.
2. `streamkeepr.recorder.segment.age` (ObservableGauge, s). Max seconds since the newest segment across active jobs; no channel label. Alert when it exceeds about 3 × `SegmentSeconds`.
3. `streamkeepr.recorder.first_media` (Histogram, s). Launch to first media per part. It shows Streamlink and Twitch start-up slowness and ad/preroll effects.
4. `streamkeepr.recorder.tool_output` (Counter) by `tool` (`ffmpeg|streamlink`) and `level` (`warning|error`, parsed from the line prefix). This turns finding 11's invisible ffmpeg warnings into a rate without raising log levels.
5. `streamkeepr.dvr.transitions` (Counter) by `from`, `to`, and `event`, all bounded enums. It gives retry and cooldown frequency without parsing logs.
6. `streamkeepr.health.status{check}` (finding 3).

### P2 — traces for operations, not just HTTP

Short-lived `StreamKeepr` spans, each a root (`parentContext: default`, as the poller does) so they never inherit a request:

- `streamkeepr.channel.{add|remove|pause|resume}`. This also parents the `/helix/users` lookup.
- `streamkeepr.eventsub.session`: connect, then welcome, then subscribe (parents the subscription POSTs). Status Error on setup failure.
- `streamkeepr.twitch.token`: acquire or refresh (parents the `oauth2/token` POST), with a `reason` tag.
- `streamkeepr.recorder.part.start` (launch to first media) and `streamkeepr.recorder.part.stop` (stop request to finalize, with the exit kind and force-kill as tags). Avoid a span for the whole part: those live for hours and only export at the end.
- `streamkeepr.export` (with a duration histogram alongside), `streamkeepr.recovery` at startup, and `streamkeepr.recording.delete`.

### P2 — other metrics

- Twitch: `streamkeepr.twitch.polls{outcome}` (the poller already computes `ok|partial|failed`), plus a gauge from Helix's `Ratelimit-Remaining` header, which nothing reads today.
- EventSub: `streamkeepr.eventsub.notifications{type}` and notification latency (`message_timestamp` to receipt).
- Chat: `streamkeepr.chat.messages` (in), `streamkeepr.chat.irc.state` gauge, IRC reconnects, bytes written, and `MaxBytesPerSession` cap hits.
- Media: a counter on `/media` and the live index by status class and file kind (`playlist|init|segment|thumb`). It stays out of HTTP metrics but shows whether playback is served and how often it 404s.

### P3 — operating the stack

- Alert rules (Grafana or Prometheus): readiness not 2xx; poll age > 2 × the active interval; EventSub disconnected for more than 5 minutes with channels watched; segment age (P1-2); health Degraded; free space trending to `MinFreeGb`. The research memo listed alerting as YAGNI. Now that the overlay exists, it is the cheapest way to make any of the signals above matter.
- Collector self-metrics into Prometheus (`service.telemetry.metrics`), plus a dashboard row for refused or failed exports.
- OTLP logs remain deferred to #197. The file pipeline works and is not the bottleneck.

## Suggested order

1. Fix findings 1 and 2. They are real bugs, small, and each gets a regression test.
2. Clean up the noise: findings 3, 4, 5, 6, 7, and 9. These are config, a view, and an enricher, with no new domain code.
3. P1 recorder metrics, then the P2 spans and metrics, then P3.
