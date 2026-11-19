# OpenTelemetry metrics and traces for StreamKeepr

Status: research memo, 2026-11-17
Issue: TBD (proposed)
Binding: no. This memo changes no code or contract. Adoption would add packages, configuration, and instrumentation; a later implementation spec or ADR should own the actual change.

Scope: **metrics and traces now; structured logging stays with Serilog (issue #197 and its own research memo).** This memo covers wiring OpenTelemetry into the single ASP.NET Core / Blazor Server app, which signals to emit, how to keep credentials and personal data out, and how to make the local backend optional. No code was changed.

## 1. Method

Sources were the official OpenTelemetry documentation and GitHub source, Microsoft Learn/.NET API documentation, the NuGet flat-container API and package contents (`.nupkg` `lib/net10.0` folders), Grafana's official documentation and repository, and Serilog's official repository and package metadata. All web sources were fetched on **2026-11-17**; links are in §13.

Repository facts were read at commit **`a33e046`**. Repository citations below are relative to `src/StreamKeepr/` unless prefixed with `tests/`. Serena's C# language server returned only the namespace for every inspected C# file in this session, so code was read with targeted `read`/`grep` calls; this is the native fallback reported per `AGENTS.md`. Markdown and configuration were read natively.

Nothing was run: no package was installed, no build or test was executed, no collector or backend was started, no Twitch credentials were used, and no live Twitch or BetterTTV endpoint was called. Package/version facts come from NuGet metadata; runtime behavior facts come from the owning source code. Items I could not verify are marked **UNKNOWN** with a verification step.

## 2. Summary and recommendation

**Recommendation: adopt manual, in-process OpenTelemetry for metrics and traces in one small opt-in slice, and keep Serilog as the only logging pipeline.** The app is one process with a handful of channels and no existing collector; the value is time-series data and causal context for operations that already have detailed logs, not a second logging system.

- **Packages:** four stable `1.21.0` packages (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`), all targeting `net10.0`. `OpenTelemetry.Instrumentation.Runtime` is optional because on .NET 10 it only adds `AddMeter("System.Runtime")`.
- **Default:** telemetry is **off** unless `Telemetry:Enabled=true`. Tests, the Mock/E2E profile, and the default `compose.yaml` stay off. `OTEL_SDK_DISABLED=true` is the standard emergency kill switch.
- **Signals:** automatic ASP.NET Core server spans and HttpClient spans/metrics, plus domain spans at operation boundaries (poll round, EventSub connect/handoff, chat connect, recorder finalize, session finalize, export, thumbnail) and about two dozen low-cardinality metrics. No per-message, per-segment, per-state-transition, or per-Blazor-event spans.
- **Security:** filter `/media`, `/_blazor`, `/signin-twitch`, and health paths from traces; disable SignalR/Blazor trace sources; rewrite `url.path` to the route template; keep channel logins, IDs, chat text, tokens, and OAuth values out of all signals. Query values are redacted by the instrumentation by default, but do not rely on that alone.
- **Backend:** OTLP/gRPC to an optional collector in an `observability` compose profile. The default single-service deployment has no new container, port, or dependency. If the collector is down, the app drops telemetry after short timeouts and keeps recording.

Explicitly out of scope (YAGNI): OTLP log export (Serilog #197), an in-app Prometheus scrape endpoint, OpenTelemetry AutoInstrumentation, Blazor/SignalR spans in v1, per-message chat tracing, Kafka, tail sampling, service mesh, alerting rules, and a full LGTM stack as the default.

## 3. Current state

At `a33e046` the app has `ILogger<T>`/`ILoggerFactory` logging only; `Program.cs:19-350` wires the host, and `src/StreamKeepr/StreamKeepr.csproj:1-24` has no OpenTelemetry or Serilog package. Health endpoints `/health/live` and `/health/ready` (`Program.cs:340-348`) are backed by `DvrHealthCheck` (`Hosting/DvrHealthCheck.cs:25-85`), `DiskSpaceHealthCheck` (`Hosting/DiskSpaceHealthCheck.cs:12-25`), and `LibraryLoadedHealthCheck` (`Hosting/LibraryLoadedHealthCheck.cs:9-13`). `DvrStatus` (`Infrastructure/Runtime/DvrStatus.cs:47-142`) and `PollSchedule` (`Infrastructure/Twitch/PollSchedule.cs:115-192`) hold cached, lock-protected snapshots suitable for gauges. One non-root container runs with `json-file` logs, `stop_grace_period: 50s`, and a 40 s shutdown budget (`Program.cs:42-45`).

The gap is **time series** (recording count, stalls/force-kills, poll age, push coverage, free disk, export/thumbnail duration), **causality** (a slow/failed finalize, handoff, or export with its children), and **baseline** (process/GC/thread-pool and Kestrel/SignalR/Blazor metrics already emitted but not collected).

| Component | Lifetime | Automatic visibility | Manual work |
|---|---|---|---|
| ASP.NET Core host | process | server spans, `http.server.*`, Kestrel | path filters, route/path redaction |
| Blazor Server circuit | per tab, minutes–hours | SignalR/circuit metrics; optional SignalR/Blazor spans | no per-event spans; `/_blazor` is long-lived |
| `DvrSupervisor` / `ChannelCoordinator` | host / per channel | nothing | recovery/finalize spans; no per-transition span |
| `RecorderJob` | per part, minutes–hours | nothing (child processes opaque) | finalize span + exit/stall metrics; no part-long span |
| `TwitchStreamPoller` / `EventSubClient` | host loop | HttpClient span/metric per Helix request | poll/connect/handoff spans; age/coverage/reconnect metrics |
| `IrcChatClient` / `ChatCaptureService` | host | nothing (raw `ClientWebSocket`) | connect span, message/drop/gap counters; no text |
| `SessionStore` / `StorageMonitor` / `ExportService` / `ThumbnailService` | singleton/host | nothing | finalize/export/grab spans, gauges/counters |

No OpenTelemetry package instruments `IHostedService`/`BackgroundService`; custom spans are required. The recorder, EventSub, and chat sockets are opaque to automatic instrumentation: child processes and raw `ClientWebSocket` frames are not traced.

## 4. Package and version baseline

NuGet flat-container metadata on 2026-11-17: the newest stable OpenTelemetry .NET line is **1.21.0**, published 2026-11-11. Inspecting each `.nupkg` shows an explicit `lib/net10.0` assembly and a `net11.0` assembly, so `net10.0` is first-class, not a fallback.

| Package | Version | Status | Needed? | Why / why not |
|---|---:|---|---|---|
| `OpenTelemetry.Extensions.Hosting` | 1.21.0 | Stable | Yes | `AddOpenTelemetry()`; hosted service at index 0; DI owns provider shutdown. |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.21.0 | Stable | Yes | OTLP/gRPC or HTTP/protobuf; reads `OTEL_EXPORTER_OTLP_*`. |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.21.0 | Stable | Yes | HTTP attributes, path filter/enrichment, SignalR/Blazor sources, built-in ASP.NET Core meters. |
| `OpenTelemetry.Instrumentation.Http` | 1.21.0 | Stable | Yes | Context propagation, filtering, `System.Net.Http`/`System.Net.NameResolution` meters. |
| `OpenTelemetry.Instrumentation.Runtime` | 1.21.0 | Stable | Optional | On .NET 9+ it is literally `AddMeter("System.Runtime")`; skip it and call `AddMeter` directly if minimizing packages. |
| `OpenTelemetry.Exporter.InMemory` | 1.21.0 | Stable, test-only | Test-only | README says “intended to be used for testing purposes.” Useful for one pipeline/filter test; not needed for domain tests. |
| `Microsoft.Extensions.Diagnostics.Testing` | 10.11.0 | Stable, test-only | Test-only | `MetricCollector<T>` with `IMeterFactory` isolation; matches the existing `Microsoft.Extensions.TimeProvider.Testing` 10.11.0 line. |

`OpenTelemetry` arrives transitively at 1.21.0; do not add a floating direct reference. Pin all OpenTelemetry packages to **1.21.0** together. The instrumentation packages declare `OpenTelemetry.Api.ProviderBuilderExtensions [1.21.0, 2.0.0)`, so a mixed 1.20/1.21 set can resolve but is not a tested combination. At 1.21.0 the instrumentation packages and the SDK share a version number; that alignment is convenient, not a permanent rule.

Rejected: **AutoInstrumentation** is a separate installer/startup-hook/profiler for unmodifiable apps; this single project needs custom spans/metrics and filters, and manual wiring is smaller and deterministic. **Prometheus.AspNetCore** is prerelease and exposes an unauthenticated endpoint; the collector already has a Prometheus exporter. **Console exporter** is noisy beside JSON-file logs. **Process/SqlClient/EF Core** is unnecessary: `System.Runtime` covers process metrics and there is no database.

## 5. Signal design

### 5.1 Traces

Automatic: ASP.NET Core requests use source `Microsoft.AspNetCore`, operation `Microsoft.AspNetCore.Hosting.HttpRequestIn`, kind Server, display name `GET /route`; HttpClient uses `System.Net.Http`/`System.Net.Http.HttpRequestOut`, kind Client. Both take W3C parents; filtered paths produce no server span. Sampling is the default `parentbased_always_on`; keep all traces in v1 because volume is low once Blazor/SignalR traces are off. HttpClient instrumentation records method/URL/status/server address—not request headers, cookies, or bodies—so Twitch bearer tokens and IRC `PASS` payloads are not captured.

Manual spans (roots unless noted; `token` is a child of the current poll/EventSub span):

| Span | Kind | Source | Attributes |
|---|---|---|---|
| `streamkeepr.twitch.poll` | Internal | `TwitchStreamPoller.RunAsync` work portion (`Infrastructure/Twitch/TwitchStreamPoller.cs:43-65`) | `channels.count`, `poll.batch_count`, `poll.outcome`; recovered batch failure is `partial`. |
| `streamkeepr.twitch.token` | Internal | `TwitchTokenProvider.GetAsync` miss (`Infrastructure/Twitch/TwitchTokenProvider.cs:26-54`) | `token.result`; never token, client id/secret, or form body. |
| `streamkeepr.eventsub.connect` | Client | `EventSubClient.RunSessionAsync` (`Infrastructure/Twitch/EventSubClient.cs:162-174`) | `eventsub.transport`, `eventsub.reconnect`, `server.address`; no reconnect URL. |
| `streamkeepr.eventsub.handoff` | Internal | `EventSubClient.StartHandoff`/completion (`EventSubClient.cs:221-243`, `:289-305`) | `eventsub.result`; 30 s grace; timeout is `Error`. |
| `streamkeepr.chat.connect` | Client | `IrcChatClient.RunConnectionAsync` connect through `001` (`Infrastructure/Twitch/Chat/IrcChatClient.cs:131-193`) | `chat.anonymous`, `chat.result`; no nick/token/channels/IRC line. |
| `streamkeepr.recorder.finalize` | Internal | `RecorderJob.RunAsync` finalize block (`Infrastructure/Media/RecorderJob.cs:191-229`) | `recorder.exit_kind`, `forced_kill`, `finalize_timed_out`; bounded 20 s + 15 s; never a part-long span. |
| `streamkeepr.session.finalize` | Internal | `DvrCommand.FinalizeSession` case in `ChannelCoordinator.ExecuteCommandsAsync` (`Infrastructure/Runtime/ChannelCoordinator.cs:173-175`), calling `SessionStore.FinalizeAsync` (`Infrastructure/Storage/SessionStore.cs:146-156`) | `session.result`; no IDs. |
| `streamkeepr.export.probe` | Internal | `ExportService.ProbeAsync` (`Infrastructure/Storage/ExportService.cs:257-301`) | `export.probe.result` (`matched`/`incompatible`/`unknown`); 30 s timeout. |
| `streamkeepr.export` | Internal | `ExportService.RunAsync` (`ExportService.cs:338-418`) | `export.outcome`, `export.hevc`; no IDs. |
| `streamkeepr.thumbnail.grab` | Internal | `ThumbnailService.TryGrabAsync` (`Infrastructure/Storage/ThumbnailService.cs:94-140`) | `thumbnail.result` (`ok`/`skipped`/`failed`); bounded per grab; 30 s timeout. |

Not traced: whole coordinator loop, EventSub session, recorder part, individual chat/EventSub messages, coordinator transitions, metadata appends, progress callbacks, `/media`, `/_blazor`.

### 5.2 Metrics

One meter, `StreamKeepr`, via `IMeterFactory` (DI lifetime, per-provider test isolation). Metrics cover time series and alertable rates; logs keep per-event detail. Every attribute below is drawn from a closed set; no channel login, stream/session/recording/export ID, title, chat text, or OAuth value appears as an attribute or metric name.

| Instrument | Type / unit | Attributes (bounded values) | Source symbol + `path:line` | Why metric | Ceiling |
|---|---|---|---|---|---|
| `streamkeepr.dvr.channels` | ObservableGauge&lt;int&gt; / `{channel}` | `streamkeepr.dvr.state` = `idle`, `recording`, `waiting_to_retry`, `cooling_down`, `paused`. The callback reports **every** state value, including zero, so a series does not go stale when the last channel leaves a state. | `DvrStatus.Current` (`Infrastructure/Runtime/DvrStatus.cs:33-40`) | Current state census; alert on stuck/recording counts. Logs describe transitions, not the current count. | 5 (states), no channel label |
| `streamkeepr.recorder.parts` | Counter&lt;long&gt; / `{part}` | `streamkeepr.recorder.exit_kind` = `exited`, `stopped`, `killed` | `RecorderJob.RunAsync` exit (`Infrastructure/Media/RecorderJob.cs:258-280`) | Rate/ratio of abnormal exits; one log line per exit. | 3 |
| `streamkeepr.recorder.part.duration` | Histogram&lt;double&gt; / `s` | `streamkeepr.recorder.exit_kind` = `exited`, `stopped`, `killed` | `RecorderJob.RunAsync` start/exit (`RecorderJob.cs:32`, `:258-280`) | Part-length distribution; no per-segment metric. | 3 |
| `streamkeepr.recorder.part.size` | Histogram&lt;long&gt; / `By` | `streamkeepr.recorder.exit_kind` = `exited`, `stopped`, `killed` | `PartFiles.TotalBytes` (`Infrastructure/Media/PartFiles.cs:77`), called at progress (`RecorderJob.cs:100`) and at exit before the empty-part delete (`RecorderJob.cs:244-254`) | Part-size distribution; useful for disk planning. Name avoids the unit word “bytes”. | 3 |
| `streamkeepr.recorder.time_to_first_media` | Histogram&lt;double&gt; / `s` | none | First `PartProducingMedia` (`RecorderJob.cs:113-118`, `:244-251`) | Detect slow starts before the 120 s stall watchdog fires. | 1 |
| `streamkeepr.recorder.stalls` | Counter&lt;long&gt; / `{stall}` | none | `stalledEnd` (`RecorderJob.cs:185-189`) | Alertable stall rate; logs carry the detail. | 1 |
| `streamkeepr.recorder.force_kills` | Counter&lt;long&gt; / `{kill}` | none | Forced/finalize-timeout path (`RecorderJob.cs:206-224`, `:258-262`) | Shutdown health trend; the log says which process needed force. | 1 |
| `streamkeepr.twitch.poll.duration` | Histogram&lt;double&gt; / `s` | `streamkeepr.twitch.poll.outcome` = `ok`, `partial`, `failed` | `TwitchStreamPoller.RunAsync` work portion (`Infrastructure/Twitch/TwitchStreamPoller.cs:43-65`) | Latency distribution per poll round; HttpClient already has per-request latency. | 3 |
| `streamkeepr.twitch.poll.failures` | Counter&lt;long&gt; / `{failure}` | none | `TwitchStreamPoller.RunAsync` when `succeeded == false` (`TwitchStreamPoller.cs:43-65`) | Poll failure rate; logs classify the error. | 1 |
| `streamkeepr.twitch.poll.age` | ObservableGauge&lt;double&gt; / `s` | none | `PollSchedule.LastPollAt` (`Infrastructure/Twitch/PollSchedule.cs:122-123`) | Poller liveness. | 1 |
| `streamkeepr.twitch.poll.success_age` | ObservableGauge&lt;double&gt; / `s` | none | `PollSchedule.LastSuccessfulPollAt` (`PollSchedule.cs:178`) | Distinguish “polling” from “polling successfully”. | 1 |
| `streamkeepr.twitch.push.coverage` | ObservableGauge&lt;double&gt; / `1` | none | `PollSchedule.Push.Covered/Watched` (`PollSchedule.cs:37`, `:115`); return `1` when `Watched == 0` | Coverage ratio; push health. | 1 |
| `streamkeepr.eventsub.notifications` | Counter&lt;long&gt; / `{notification}` | `streamkeepr.eventsub.type` = `online`, `offline`; `streamkeepr.eventsub.result` = `routed`, `ignored`, `invalid` | `EventSubClient.HandleNotificationAsync` (`Infrastructure/Twitch/EventSubClient.cs:504-620`) | Event flow and routing failures; no per-message span. | 6 (2 types × 3 results) |
| `streamkeepr.eventsub.reconnects` | Counter&lt;long&gt; / `{reconnect}` | `streamkeepr.eventsub.transport` = `WebSocket`, `Conduit` | `EventSubClient.RunAsync`/handoff (`EventSubClient.cs:96-128`, `:289-305`) | Instability trend; logs carry the reason. | 2 |
| `streamkeepr.eventsub.connected` | ObservableGauge&lt;int&gt; / `1` | `streamkeepr.eventsub.transport` = `WebSocket`, `Conduit`, `Mock` | `IEventSubClient.Transport` (`Infrastructure/Twitch/IEventSubClient.cs:8`) + `PushStatus.HasSession` (`PollSchedule.cs:44-49`) | 0/1 connection state; keepalives should not create log/metric noise. | 3 |
| `streamkeepr.chat.messages` | Counter&lt;long&gt; / `{message}` | `streamkeepr.chat.source` = `irc-token`, `irc-anonymous`, `mock` | `ChatCaptureService.Route` (`Infrastructure/Twitch/Chat/ChatCaptureService.cs:124-146`); values `ChatSources` (`Infrastructure/Storage/ChatStorageModels.cs:80-86`) | Volume only; never text, author, or message ID. | 3 |
| `streamkeepr.chat.dropped` | Counter&lt;long&gt; / `{message}` | none | `ChatCaptureService.Route` on `ChatSourceEvent.Dropped` (`ChatCaptureService.cs:124-146`; `Infrastructure/Storage/ChatSourceEvents.cs:72`); add `Dropped.Count` | Loss rate from backlog overflow; logs carry ranges. | 1 |
| `streamkeepr.chat.gaps` | Counter&lt;long&gt; / `{gap}` | `streamkeepr.chat.gap.reason` = `irc-disconnected`, `queue-full` | `ChatCaptureService.EndOutage` (`ChatCaptureService.cs:148-155`); values `ChatGapReasons` (`ChatStorageModels.cs:91-95`) | Outage rate; logs carry start/end. | 2 |
| `streamkeepr.chat.queue.depth` | ObservableGauge&lt;int&gt; / `{event}` | none | New `IChatSource.Runtime.QueueDepth` read-only snapshot member (`Infrastructure/Twitch/Chat/IChatSource.cs:5-13`); `IrcChatClient` reads `ChatEventQueue.PendingCount` (`ChatEventQueue.cs:35-60`) under the existing short lock, `MockChatSource` returns `0`; no I/O | Early warning before drops; the drop counter is after the fact. | 1 |
| `streamkeepr.chat.connected` | ObservableGauge&lt;int&gt; / `1` | none | New `IChatSource.Runtime.Connected` read-only snapshot member (`Infrastructure/Twitch/Chat/IChatSource.cs:5-13`); `IrcChatClient` reports `State == ChatConnectionState.Live` (`IrcChatClient.cs:303-312`), `MockChatSource` reports its enabled state | 0/1 IRC state; state transitions are already logged. | 1 |
| `streamkeepr.storage.free` | ObservableGauge&lt;long&gt; / `By` | none | `StorageMonitor.Current.FreeBytes` (`Infrastructure/Storage/StorageMonitor.cs:74-81`) | Disk trend/alerting; unit is in the unit field, not the name. | 1 |
| `streamkeepr.storage.total` | ObservableGauge&lt;long&gt; / `By` | none | `StorageMonitor.Current.TotalBytes` (`StorageMonitor.cs:74-81`) | Denominator for utilization; total changes rarely. | 1 |
| `streamkeepr.export.jobs` | Counter&lt;long&gt; / `{job}` | `streamkeepr.export.outcome` = `succeeded`, `failed`, `cancelled` | `ExportService.RunAsync` (`Infrastructure/Storage/ExportService.cs:338-418`) | Success/failure rate; logs carry IDs and error codes. | 3 |
| `streamkeepr.export.duration` | Histogram&lt;double&gt; / `s` | `streamkeepr.export.outcome` = `succeeded`, `failed`, `cancelled` | `ExportService.RunAsync` (`ExportService.cs:338-418`) | Performance trend for user-triggered exports. | 3 |
| `streamkeepr.export.active` | ObservableGauge&lt;int&gt; / `{job}` | none | New `ExportService.ActiveCount` accessor under `_gate` (`ExportService.cs:125-141`, `:424-428`) | Queue/worker load; the service allows one running job plus queued jobs. | 1 |
| `streamkeepr.thumbnail.grabs` | Counter&lt;long&gt; / `{grab}` | `streamkeepr.thumbnail.result` = `ok`, `skipped`, `failed` | `ThumbnailService.TryGrabAsync` (`Infrastructure/Storage/ThumbnailService.cs:94-140`) | Background success rate without a log per sweep. | 3 |
| `streamkeepr.startup.recovery.duration` | Histogram&lt;double&gt; / `s` | none | `DvrSupervisor.StartAsync` around `RecoveryService.RecoverAsync` (`Infrastructure/Runtime/DvrSupervisor.cs:36-49`) | Startup/recovery latency; one sample per process start. | 1 |

`AddMeter("System.Runtime")` adds process/GC/thread-pool/exception metrics. There is no `Microsoft.AspNetCore.SignalR` meter in .NET 10; SignalR metrics are under `Microsoft.AspNetCore.Http.Connections`. `dotnet.health_check.*` requires `AddTelemetryHealthCheckPublisher`, not enabled here. See §5.5 for the exact ASP.NET Core and HTTP meter lists.

`StreamKeeprRuntimeMetrics` must depend on interfaces registered in both real and Mock profiles (`IEventSubClient`, `IChatSource`, `ExportService`), not concrete `EventSubClient`/`IrcChatClient`, or the Mock host fails to construct the metrics service even with telemetry disabled.

### 5.3 Do not instrument

- Per chat message/IRC line, per EventSub notification, per coordinator transition, per segment/progress callback: events or hot-loop steps; metrics + logs suffice.
- Whole recorder part/EventSub session/Blazor circuit: long-lived spans; instrument bounded finalize/connect phases.
- Per Blazor event/SignalR invocation: `Components.HandleEvent` is per UI event, and `SignalRServerActivitySource.InvocationIn` is per hub invocation (Blazor Server dispatches UI events as hub invocations). Disable the trace sources; use `aspnetcore.components.handle_event.duration`.
- `streamkeepr.dvr.transitions` counter: per-channel logs already record `{From} -> {To}`; a counter would add up to 25 series without answering a new question.
- `streamkeepr.library.loaded` gauge: `/health/ready` already exposes binary readiness; duplicating it as a metric adds no diagnostic value.
- `/media`, `/health/*`, `/_blazor`: long-lived or Docker-polled; filter traces and disable HTTP metrics.

### 5.4 Naming and API rules

- One `ActivitySource` and one `Meter`, both `StreamKeepr`, in `Infrastructure/Telemetry/StreamKeeprTelemetry.cs` as a DI singleton. A separate `StreamKeeprRuntimeMetrics` singleton owns observable gauges and depends on `DvrStatus`/`PollSchedule`/`StorageMonitor`, avoiding a cycle; register it as a no-work hosted service (or resolve it once after `Build()`).
- Use `IMeterFactory.Create(new MeterOptions("StreamKeepr") { Version = ... })`, not `new Meter(...)`. `TagList` only for 4–8 tags; one or two tags as `KeyValuePair`. Use static lower-case strings from `switch`, not `Enum.ToString()` on hot paths.
- Background roots: `StartActivity(name, kind, parentContext: default)`; set `Activity.Current = null` at the top of `ChannelCoordinator.RunAsync` (`ChannelCoordinator.cs:100`) because `StartChannel` can run inside an HTTP request (`DvrSupervisor.cs:221-236`).
- Use parent/child for synchronous work; background loops are roots. Links are only for causal context captured across a queue (for example an export request); do not add links in v1.
- Leave `Unset` for expected handled outcomes; use `SetStatus(ActivityStatusCode.Error, "stable_code")` for finalize/export/connect failures. `OpenTelemetry.Trace.ActivityExtensions.RecordException` is **obsolete** since 1.10.0; use `activity.AddException(ex)`. No IDs, titles, logins, chat text, OAuth values, tokens, or cookies in attributes; no span events in loops.

### 5.5 Verified API notes (1.21.0 / .NET 10)

- `AspNetCoreTraceInstrumentationOptions` has `EnableAspNetCoreSignalRSupport`, `EnableRazorComponentsSupport`, `Filter`, `EnrichWithHttpResponse`, and `DisableUrlQueryRedaction`; the first two default to `true`.
- `AddAspNetCoreInstrumentation()` on the metrics builder adds `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `Microsoft.AspNetCore.Http.Connections`, `Microsoft.AspNetCore.Routing`, `Microsoft.AspNetCore.Diagnostics`, `Microsoft.AspNetCore.RateLimiting`, `Microsoft.AspNetCore.Components`, `Microsoft.AspNetCore.Components.Server.Circuits`, `Microsoft.AspNetCore.Components.Lifecycle`, `Microsoft.AspNetCore.Authorization`, `Microsoft.AspNetCore.Authentication`, `Microsoft.AspNetCore.Identity`, and `Microsoft.AspNetCore.MemoryPool`.
- `AddHttpClientInstrumentation()` on the metrics builder adds `System.Net.Http` and `System.Net.NameResolution`.
- `OpenTelemetry.Trace.ActivityExtensions.RecordException` is obsolete in 1.21.0; `Activity.AddException` is the replacement.
- `IHttpMetricsTagsFeature.MetricsDisabled` and the `DisableHttpMetrics()` endpoint convention exist in .NET 10; the middleware is the fallback for non-endpoint paths.
- `OTEL_SDK_DISABLED` is supported in 1.21.0 and evaluated at provider construction.

## 6. Wiring and configuration

Key `Program.cs` calls (keep the existing top-level structure):

```csharp
builder.Services.AddSingleton<StreamKeeprTelemetry>();
builder.Services.AddHostedService<StreamKeeprRuntimeMetrics>(); // no background work

if (builder.Configuration.GetValue("Telemetry:Enabled", false))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(StreamKeeprTelemetry.ServiceName, serviceVersion: StreamKeeprTelemetry.ServiceVersion,
                        autoGenerateServiceInstanceId: false)
            .AddAttributes(new Dictionary<string, object> {
                ["deployment.environment.name"] = TelemetryEnvironment.Name(builder.Environment) }))
        .WithTracing(t => t
            .AddSource(StreamKeeprTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation(o => {
                o.Filter = TelemetryPaths.ShouldTrace;
                o.EnableAspNetCoreSignalRSupport = false;
                o.EnableRazorComponentsSupport = false;
                o.EnrichWithHttpResponse = TelemetryPaths.RewriteUrlPath; })
            .AddHttpClientInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(m => m
            .AddMeter(StreamKeeprTelemetry.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("System.Runtime")
            .AddOtlpExporter());
}
```

Do **not** call `.WithLogging()` or `builder.Logging.AddOpenTelemetry(...)`; `AddOpenTelemetry()` plus `WithTracing`/`WithMetrics` registers no `ILoggerProvider`. Only `WithLogging`/logging-builder `AddOpenTelemetry` does. `AddAspNetCoreInstrumentation()` already adds all built-in ASP.NET Core meters; do not also `AddMeter("Microsoft.AspNetCore.Hosting")`. `AddHttpClientInstrumentation()` already adds `System.Net.Http` and `System.Net.NameResolution`.

Path policy (single authority for traces and metrics):

```csharp
private static readonly string[] Excluded = ["/health/live", "/health/ready", "/media", "/_blazor", "/signin-twitch"];
public static bool ShouldTrace(HttpContext c) => !Excluded.Any(p => c.Request.Path.StartsWithSegments(p));
public static bool DisableHttpMetrics(PathString p) => Excluded.Any(x => p.StartsWithSegments(x));
public static void RewriteUrlPath(Activity a, HttpResponse r)
{
    if (a.GetTagItem("http.route") is string route && route.Length > 0) a.SetTag("url.path", route);
    else a.SetTag("url.path", "/[unrouted]");
}
```

Why: `/signin-twitch` is the OAuth callback (`Infrastructure/Twitch/AdminAuth.cs:21-22`) with `code`/`state`; values are redacted by default, but filtering removes the span. `/media` is middleware (`Program.cs:307`), and `LiveIndexEndpoint` path `/media/{channel}/{id}/index.m3u8` (`Hosting/LiveIndexEndpoint.cs:12`) contains a login and recording ID. `/_blazor` is the Blazor WebSocket upgrade; `/health/*` is Docker-polled. Apply the metrics exclusion before `UseAuthentication`:

```csharp
app.Use(async (context, next) => {
    if (TelemetryPaths.DisableHttpMetrics(context.Request.Path)
        && context.Features.Get<IHttpMetricsTagsFeature>() is { } f) f.MetricsDisabled = true;
    await next(); });
```

Also call `.DisableHttpMetrics()` on the health endpoints (`Program.cs:340-348`). Startup guard: if telemetry is on and either `OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION` or `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` is true, throw. The app never needs unredacted query values.

| Key / env var | Default | Recommendation | Notes |
|---|---|---|---|
| `Telemetry:Enabled` / `Telemetry__Enabled` | `false` | primary switch | False means no providers. |
| `OTEL_SDK_DISABLED` | unset | emergency only | Since .NET OTel 1.15.0; evaluated once at provider construction; providers become no-ops. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `_PROTOCOL` | `http://localhost:4317` / `grpc` | `http://otel-collector:4317` / `grpc` | HTTP/protobuf uses `:4318`; the SDK appends `/v1/traces` and `/v1/metrics`. |
| `OTEL_EXPORTER_OTLP_TIMEOUT` / `OTEL_BSP_EXPORT_TIMEOUT` | `10000` / `30000` ms | `3000` / `3000` | Signal-specific `_TRACES_TIMEOUT`/`_METRICS_TIMEOUT` override. |
| `OTEL_BSP_SCHEDULE_DELAY` / `OTEL_METRIC_EXPORT_INTERVAL` | `5000` / `60000` ms | `2000` / `30000` | Responsiveness vs local load. |
| `OTEL_METRIC_EXPORT_TIMEOUT` | `30000` ms | `3000` | Bounds final metric flush. |
| `OTEL_TRACES_SAMPLER` | `parentbased_always_on` | leave default | Ratio sampling hurts low-volume debugging. |
| `OTEL_SERVICE_NAME` / `OTEL_RESOURCE_ATTRIBUTES` | `unknown_service` / empty | do not set | `OTEL_SERVICE_NAME` will **not** override the code’s `service.name=streamkeepr`: the env detector is added first and the later `AddService`/`AddAttributes` wins. If an operator must override it, the code must read the env var itself or omit `AddService`. Use `OTEL_RESOURCE_ATTRIBUTES` only for extra attributes the code does not set. |
| `OTEL_EXPORTER_OTLP_HEADERS` | unset | collector/secret only | Never commit; self-diagnostics redacts it, but it is still a secret. |

`OTEL_TRACES_EXPORTER` and `OTEL_METRICS_EXPORTER` are **not** read by manually configured `AddOpenTelemetry()` in 1.21.0; they appear only in the SDK self-diagnostics policy. `OTEL_TRACES_EXPORTER=none` will not remove a programmatic OTLP exporter. Use `Telemetry:Enabled`/`OTEL_SDK_DISABLED`.

SDK defaults: batch span processor queue 2048, max batch 512, schedule delay 5 s, export timeout 30 s; periodic metric reader interval 60 s, timeout 30 s. The OTel hosted service is inserted at index 0, so it starts before preflight/recovery. Its `StopAsync` is a no-op; DI disposes the providers after hosted services stop, and the 3 s export timeouts keep the final flush inside the 50 s compose grace. No explicit `TracerProvider.Shutdown`/`MeterProvider.Shutdown` call is needed; DI provider disposal invokes shutdown. That is why the final recorder-finalize spans are still recorded.

Resource attributes: `service.name=streamkeepr`; `service.version` from `AssemblyInformationalVersionAttribute`; `service.instance.id` is **not set automatically** (`ResourceBuilder.CreateDefault()` adds only the default `service.name`, telemetry SDK attributes, and the env detector). For this single container leave it unset. Set it only if multiple instances must be distinguished, and make it stable per instance (for example an operator-supplied instance name), not per process, or metric series churn on every restart. `AddService` can generate an ID by default, so the sketch passes `autoGenerateServiceInstanceId: false`. `deployment.environment.name` comes from `builder.Environment.EnvironmentName` (`production`/`development`/`test`; map Mock/Testing to `test`); use the current name `deployment.environment.name`, not `deployment.environment`. Log once after `Build()` with service/version/environment/endpoint, sanitized to scheme+authority; never headers/tokens.

```yaml
    environment:
      Telemetry__Enabled: ${STREAMKEEP_TELEMETRY:-false}
      OTEL_EXPORTER_OTLP_ENDPOINT: ${OTEL_EXPORTER_OTLP_ENDPOINT:-http://otel-collector:4317}
      OTEL_EXPORTER_OTLP_PROTOCOL: grpc
      OTEL_EXPORTER_OTLP_TIMEOUT: "3000"
      OTEL_BSP_EXPORT_TIMEOUT: "3000"
      OTEL_METRIC_EXPORT_INTERVAL: "30000"
      OTEL_METRIC_EXPORT_TIMEOUT: "3000"
```

## 7. Collector and local stack

Direct app→backend is simplest but puts credentials/churn in the app and gives no retry/filter/fan-out. **App→collector→backend is recommended:** the app offloads with a 3 s timeout, the collector batches/retries (default queue ~1000 batches, up to 5 min), filters, and hides backend changes. OTel recommends a collector for retries, batching, and sensitive-data filtering.

Keep `compose.yaml` unchanged. Add `compose.observability.yaml` with an `observability` profile and run `STREAMKEEP_TELEMETRY=true docker compose -f compose.yaml -f compose.observability.yaml --profile observability up -d`.

Pinned images (2026-11-17): `otel/opentelemetry-collector-contrib:0.165.0`, `grafana/tempo:3.1.1`, `prom/prometheus:v3.16.0`, `grafana/grafana:13.3.1`. Bind published ports to `127.0.0.1` only; Tempo has no auth, and Prometheus/Grafana defaults are not internet-safe.

Minimal collector config:

```yaml
receivers:
  otlp:
    protocols:
      grpc: { endpoint: 0.0.0.0:4317 }
      http: { endpoint: 0.0.0.0:4318 }
processors:
  memory_limiter: { check_interval: 1s, limit_mib: 256, spike_limit_mib: 64 }
  batch: {}
exporters:
  otlp_grpc/tempo: { endpoint: tempo:4317, tls: { insecure: true } }
  prometheus: { endpoint: 0.0.0.0:8889 }
service:
  pipelines:
    traces: { receivers: [otlp], processors: [memory_limiter, batch], exporters: [otlp_grpc/tempo] }
    metrics: { receivers: [otlp], processors: [memory_limiter, batch], exporters: [prometheus] }
```

Leave `resource_to_telemetry_conversion` disabled. The Prometheus exporter exposes `target_info` for resource attributes; copying `service.version` (and any later `service.instance.id`) onto every series would create new series on every deploy/instance change. Enable it only if a dashboard genuinely needs those labels and the churn is acceptable.

Use the official Tempo single-binary example for `observability/tempo.yaml` (local backend, WAL/blocks under `/var/tempo`, 7-day retention, usage reporting off). Prometheus: 30 s scrape, target `otel-collector:8889`; add Prometheus (`http://prometheus:9090`) and Tempo (`http://tempo:3200`) as Grafana data sources. .NET dashboards: `https://aka.ms/dotnet/grafana-dashboards`.

**Quick alternative.** `grafana/otel-lgtm:0.41.0` bundles Collector, Prometheus, Tempo, Loki, Pyroscope, and Grafana in one container. Its README says it is **“intended for development, demo, and testing environments”**; it is a disposable first look, not the always-on recommendation. It accepts OTLP on 4317/4318 and serves Grafana on 3000; bind to `127.0.0.1`.

Resource use is **UNKNOWN** on the owner’s host; verify with `docker stats` and set `mem_limit`/volumes. If the collector is down, the app’s 3 s export times out and drops; the collector queue only helps after delivery. The app never blocks or fails a request because telemetry failed. Do not add Kafka, tail sampling, or a service mesh.

## 8. Serilog seam (integration only)

- **No double export.** `AddOpenTelemetry()` without `.WithLogging()` registers the telemetry hosted service and provider builders, not an `ILoggerProvider`. `WithLogging`/`builder.Logging.AddOpenTelemetry` is the only path that adds one. Test that `IEnumerable<ILoggerProvider>` contains no OpenTelemetry provider. Serilog #197 is unaffected.
- **Correlation.** `Serilog.Extensions.Logging` 10.0.0 reads `Activity.Current` in `SerilogLogger.Write` and calls `LogEvent.UnstableAssembleFromParts(..., traceId, spanId)`; `LogEvent.TraceId`/`SpanId` are populated automatically (`src/Serilog.Extensions.Logging/Extensions/Logging/SerilogLogger.cs:160-166`). Sinks that understand trace context (Seq, `Serilog.Sinks.OpenTelemetry`) can use it. `LoggerFactoryOptions.ActivityTrackingOptions` adds trace context to logging scopes, but Serilog’s provider surfaces scopes as a `Scope` property rather than top-level fields; the native `LogEvent.TraceId`/`SpanId` path is better. A plain console/file sink needs a small enricher or output template; that is a #197 decision.
- **Do not use `Serilog.Enrichers.Span`.** It is deprecated in its README and issue #332 (Serilog now has native span data), maintained by a single community author, last release 3.1.0 in 2023.
- **Ownership.** `Serilog.Extensions.Logging` is “Microsoft, Serilog Contributors”, Apache-2.0; `Serilog.Sinks.OpenTelemetry` is Serilog Contributors, Apache-2.0; OpenTelemetry packages are “OpenTelemetry Authors”, Apache-2.0.
- **What OTel must expose:** stable source name `StreamKeepr`, normal `Activity.Current` lifetime, and no manual trace/span ID plumbing. Do not create a second synthetic ID or put trace IDs in metric attributes.
- **If OTLP logs are wanted later:** one coherent path is `Serilog.Sinks.OpenTelemetry` 4.2.0 writing OTLP logs to the same collector, with `IncludedData.TraceIdField | SpanIdField` (defaults). Do **not** also enable `.WithLogging()`. Deferred; belongs to #197.

## 9. Testing and verification

- **Spans:** `ActivityListener` with `ShouldListenTo` exact source `StreamKeepr`, `Sample = AllDataAndRecorded`, collect in `ActivityStopped`. It is process-global; put telemetry tests in one xunit collection with `DisableParallelization = true`, dispose the listener at the end, and do not assert counts after disposal.
- **Metrics:** `MetricCollector<T>` (`Microsoft.Extensions.Diagnostics.Testing`) using the app’s `IMeterFactory` and meter `StreamKeepr`. `IMeterFactory` isolates meters per service provider; this is why the app must not use a static `Meter`.
- **Raw measurements:** `MeterListener` with `InstrumentPublished` filtering and `SetMeasurementEventCallback`; same non-parallel rule; call `RecordObservableInstruments()`.
- **Pipeline:** `OpenTelemetry.Exporter.InMemory` 1.21.0 is stable but explicitly test-only; use it for one filter/redaction/resource test, not for domain tests.

A span test collects `ActivityStopped` events in a `ConcurrentBag<Activity>`, calls the operation, then asserts `OperationName`, kind, tags, and `Status` on the single `streamkeepr.recorder.finalize` span.

Add `"Telemetry:Enabled": "false"` to `StreamKeeprAppFactory` defaults (`tests/StreamKeepr.Tests/Hosting/StreamKeeprAppFactory.cs:56-65`) and to `MockEnvironment.Configure`’s in-memory dictionary (`src/StreamKeepr/Mock/MockEnvironment.cs:89-99`; the in-memory provider is added last, so Mock/E2E cannot inherit a real endpoint); add `"Telemetry": { "Enabled": false }` to `appsettings.json`. Tests: default-off resolves null `TracerProvider`/`MeterProvider`; no OpenTelemetry `ILoggerProvider`; `ShouldTrace` false for `/signin-twitch`, `/media/x`, `/_blazor`, `/health/live`; `RewriteUrlPath` yields `http.route`.

Manual checks: with `STREAMKEEP_TELEMETRY=true`, inspect the startup log; find a `streamkeepr.twitch.poll` trace with a `GET` child and no `user_login`; confirm `/health/live` leaves no trace/metric; record/stop and check `recorder.finalize` ≤ 35 s; stop the collector and confirm the app serves and recovers; search traces for `code=`, `state=`, `Bearer`, `client_secret`, and chat text and find none.

## 10. Risks and edge cases

- **OAuth `code`/`state` (low after defaults / high):** keep query redaction; filter `/signin-twitch`; startup guard against the two `*_DISABLE_URL_QUERY_REDACTION` vars; test.
- **Long `/media`/`/_blazor` spans (high if unfiltered / medium):** trace filter; `IHttpMetricsTagsFeature.MetricsDisabled`; `DisableHttpMetrics()` for health.
- **Shutdown flush vs 40 s host / 50 s compose (medium / medium):** 3 s OTLP/BSP/metric timeouts keep the final flush inside the 50 s compose grace; raise `stop_grace_period` to 60 s only if extra margin is wanted, and verify a stop with an active recorder.
- **Collector down (high over time / low):** app drops after 3 s; collector queue retries ~5 min; smoke-test with collector stopped.
- **Cardinality (low with this design / medium):** no IDs/logins/titles/chat text; default 2000 points/metric; overflow becomes `otel.metric.overflow=true`.
- **Hot-path overhead (low / medium):** chat counters only; no per-message/per-progress instruments; one tag per measurement.
- **Blazor circuit invisibility (certain / low–medium):** per-event work is covered by `aspnetcore.components.handle_event.duration`; circuit counts by Components/SignalR metrics; logs cover details.
- **`url.path` IDs (medium / medium):** rewrite to `http.route`; filter `/media`; never add IDs as attributes.
- **Channel logins (medium if added / medium privacy):** none by default; later, a config-gated span attribute only, never a metric label.
- **Temporality/SDK drift/supply chain (low–medium):** keep cumulative; pin 1.21.0; bump together; NuGet.org only; optional lock file.
- **WebSocket span boundary (UNKNOWN / low):** whether the `/_blazor` HTTP span ends at upgrade or circuit close is not verified; the path filter makes it moot.
- **Filter child spans (UNKNOWN / low):** whether `Filter=false` also drops the OAuth token HttpClient child in 1.21.0 is not verified; test with the in-memory exporter. The query is redacted either way and the body is never captured.
- **Public exposure (low if localhost-bound / high):** bind all optional ports to `127.0.0.1`; Tempo has no auth; VPN or authenticating proxy for remote access.

## 11. Phased implementation plan

Each slice is independently shippable and reversible.

- **Slice 1 — tracer bullet.** Add the four packages and `Infrastructure/Telemetry/{StreamKeeprTelemetry,TelemetryPaths,TelemetryEnvironment}.cs`; wire `Program.cs:19-45` and `:178` (hosted metrics service), `:280-307` (metrics-disable middleware), `:340-348` (`.DisableHttpMetrics()`); update `appsettings.json`, `compose.yaml`, `MockEnvironment.cs`, `StreamKeeprAppFactory.cs`. Enable OTLP/gRPC with filters, path rewrite, SignalR/Blazor traces off, and one `streamkeepr.twitch.poll` span (`TwitchStreamPoller.cs:36-68`). **Accept:** poll trace with a `GET` child; no span for `/health/live`, `/media`, `/_blazor`, `/signin-twitch`; app works with collector stopped; default-off/filter tests.
- **Slice 2 — metrics baseline.** Add `StreamKeeprRuntimeMetrics` (gauges from `DvrStatus.Current`, `PollSchedule`, `StorageMonitor.Current`); instrument `RecorderJob.cs:113-118,185-189,258-280`, `ChatCaptureService.cs:124-155`, `StorageMonitor.cs:89-122`, `ExportService.cs:338-418`, `ThumbnailService.cs:94-140`. **Accept:** Grafana shows DVR/recorder/poll/storage/HTTP series; no login/ID/chat text; bounded series; `MetricCollector` tests.
- **Slice 3 — domain span depth.** EventSub connect/handoff (`EventSubClient.cs:162-174,221-243,289-305`), chat connect (`IrcChatClient.cs:131-162`), session finalize (`ChannelCoordinator.cs:173-175`), export (`ExportService.cs:257-301,338-418`), recovery (`DvrSupervisor.cs:36-49`); notification/reconnect counters; `Activity.Current = null` at the coordinator loop. **Accept:** one handoff/finalize span, no part-long span, no sensitive attributes.
- **Slice 4 — optional profile.** Add `compose.observability.yaml` and `observability/{otel-collector,tempo,prometheus,grafana-datasources}.yaml`; four services, localhost-only ports, volumes, limits, 7-day Tempo/30-day Prometheus; document `grafana/otel-lgtm` as dev-only. **Accept:** default compose unchanged; profile works; collector stop does not stop the app; no off-host port.

**Do not build:** AutoInstrumentation; `.WithLogging()`/OTLP logs; in-app Prometheus; per-message/per-transition/per-segment/per-Blazor-event spans; part-long/session-long spans; channel logins/IDs/chat text/tokens/OAuth values in any signal; Kafka/tail sampling/service mesh/custom exporters; alerting/dashboards in v1.

## 12. Open questions / owner decisions

- **Backend:** four-service collector + Tempo/Prometheus/Grafana for always-on home use; `grafana/otel-lgtm` only for a first look. Grafana labels LGTM development/demo/testing; the split stack is supportable and persistent.
- **Channel logins:** no by default; later, a config-gated **span** attribute only, never a metric label. The watch list is personal data; logs already name the channel and carry `TraceId`.
- **Collector:** yes, at least locally; the app points at an OTLP collector endpoint. It keeps backend credentials out of the app and provides retry/batch/filter.
- **Blazor/SignalR traces:** no in v1; metrics only. Per-event spans are high-volume, and the circuit span name includes the circuit ID.
- **Retention:** 7 days traces, 30 days metrics, 2 GB cap; verify with real disk use.

## 13. Sources

All accessed **2026-11-17**.

### OpenTelemetry

1. OpenTelemetry .NET instrumentation, metric instruments, metrics/traces best practices: https://opentelemetry.io/docs/languages/dotnet/instrumentation/ ; https://opentelemetry.io/docs/languages/dotnet/metrics/instruments/ ; https://opentelemetry.io/docs/languages/dotnet/metrics/best-practices/ ; https://opentelemetry.io/docs/languages/dotnet/traces/best-practices/
2. OpenTelemetry SDK and OTLP exporter configuration: https://opentelemetry.io/docs/languages/sdk-configuration/general/ ; https://opentelemetry.io/docs/languages/sdk-configuration/otlp-exporter/
3. OpenTelemetry Collector, configuration, resiliency: https://opentelemetry.io/docs/collector/ ; https://opentelemetry.io/docs/collector/configuration/ ; https://opentelemetry.io/docs/collector/resiliency/
4. Semantic conventions: metrics, naming, tracing API, deployment resource: https://opentelemetry.io/docs/specs/semconv/general/metrics/ ; https://opentelemetry.io/docs/specs/semconv/general/naming/ ; https://opentelemetry.io/docs/specs/otel/trace/api/ ; https://opentelemetry.io/docs/specs/semconv/resource/deployment-environment/
5. OpenTelemetry .NET SDK source, tag `core-1.21.0`: https://github.com/open-telemetry/opentelemetry-dotnet/tree/core-1.21.0/src
6. OpenTelemetry .NET contrib instrumentation source: https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tree/main/src
7. In-memory exporter README: https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/src/OpenTelemetry.Exporter.InMemory
8. `OTEL_SDK_DISABLED`: https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.21.0/src/OpenTelemetry/SdkConfigDefinitions.cs

### Microsoft

9. ASP.NET Core built-in metrics, HTTP, Blazor: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-aspnetcore ; https://learn.microsoft.com/en-us/aspnet/core/metrics/http?view=aspnetcore-10.0 ; https://learn.microsoft.com/en-us/aspnet/core/metrics/blazor?view=aspnetcore-10.0
10. ASP.NET Core metrics overview: https://learn.microsoft.com/en-us/aspnet/core/metrics/overview?view=aspnetcore-10.0
11. Runtime, System.Net, extensions metrics: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime ; https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-system-net ; https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-diagnostics
12. Creating metrics (observable callbacks, IMeterFactory, MetricCollector): https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation
13. `ActivityTrackingOptions`: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.activitytrackingoptions?view=net-10.0
14. `DisableHttpMetrics` and `IHttpMetricsTagsFeature.MetricsDisabled`: https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.builder.httpmetricsendpointconventionbuilderextensions.disablehttpmetrics?view=aspnetcore-10.0 ; https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.features.ihttpmetricstagsfeature.metricsdisabled?view=aspnetcore-10.0
15. ASP.NET Core hosting, Blazor, and SignalR activity sources: https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Hosting/Hosting/src/Internal/HostingApplicationDiagnostics.cs ; https://github.com/dotnet/aspnetcore/tree/release/10.0/src/Components ; https://github.com/dotnet/aspnetcore/blob/release/10.0/src/SignalR/server/Core/src/Internal/SignalRServerActivitySource.cs

### Grafana

16. `grafana/otel-lgtm` README: https://github.com/grafana/docker-otel-lgtm
17. Tempo local deployment, single-binary compose, configuration: https://grafana.com/docs/tempo/latest/set-up-for-tracing/setup-tempo/deploy/locally/ ; https://github.com/grafana/tempo/tree/main/example/docker-compose/single-binary ; https://grafana.com/docs/tempo/latest/configuration/

### Serilog

18. Serilog extensions logging, OTLP sink, deprecated span enricher and issue #332: https://github.com/serilog/serilog-extensions-logging ; https://github.com/serilog/serilog-sinks-opentelemetry ; https://github.com/RehanSaeed/Serilog.Enrichers.Span ; https://github.com/RehanSaeed/Serilog.Enrichers.Span/issues/332

### NuGet

19. NuGet: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http` 1.21.0: https://www.nuget.org/packages/OpenTelemetry.Extensions.Hosting/1.21.0 ; https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol/1.21.0 ; https://www.nuget.org/packages/OpenTelemetry.Instrumentation.AspNetCore/1.21.0 ; https://www.nuget.org/packages/OpenTelemetry.Instrumentation.Http/1.21.0
20. NuGet: `OpenTelemetry.Instrumentation.Runtime` 1.21.0, `OpenTelemetry.Exporter.InMemory` 1.21.0, `Microsoft.Extensions.Diagnostics.Testing` 10.11.0, `Serilog.Sinks.OpenTelemetry` 4.2.0: https://www.nuget.org/packages/OpenTelemetry.Instrumentation.Runtime/1.21.0 ; https://www.nuget.org/packages/OpenTelemetry.Exporter.InMemory/1.21.0 ; https://www.nuget.org/packages/Microsoft.Extensions.Diagnostics.Testing/10.11.0 ; https://www.nuget.org/packages/Serilog.Sinks.OpenTelemetry/4.2.0
