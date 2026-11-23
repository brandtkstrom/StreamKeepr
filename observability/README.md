# Observability stack

`compose.observability.yaml` at the repository root layers an OpenTelemetry Collector,
Prometheus, Loki, Tempo, and Grafana onto the regular build-from-checkout deployment in
`compose.yaml`. The base file keeps every production setting (secrets, volumes, ports);
the overlay switches telemetry on and adds the observability services. The binding rules
are in [`docs/hosting-contract.md`](../docs/hosting-contract.md) ("Observability overlay").

## Start

1. Create the Grafana admin password file next to the Twitch secrets. See the main README
   for those.

   ```bash
   cp secrets/grafana_admin_password.txt.example secrets/grafana_admin_password.txt
   ```

   Replace its contents with a password. On Linux, Grafana (UID 472) must be able to read it:
   `sudo chown 472:472 secrets/grafana_admin_password.txt && sudo chmod 400 secrets/grafana_admin_password.txt`.
   Grafana reads it once, when it first creates its database. To change it later, change it
   in Grafana.

2. Start the app with the overlay:

   ```bash
   docker compose -f compose.yaml -f compose.observability.yaml up -d --build
   ```

   To skip the `-f` flags, put `COMPOSE_FILE=compose.yaml:compose.observability.yaml` in `.env`.
   Passing the overlay is the opt-in. It has no Compose profile, and it sets
   `Telemetry__Enabled=true` whatever `STREAMKEEP_TELEMETRY` says.

| What | Where |
| --- | --- |
| Stream Keepr | http://localhost:8080 (`STREAMKEEP_PORT`) |
| Grafana | http://localhost:3000 (`GRAFANA_PORT`), user `admin` (`GRAFANA_ADMIN_USER`) |
| Prometheus | http://localhost:9090 (`PROMETHEUS_PORT`) |

Grafana and Prometheus listen on 127.0.0.1 only. Loki, Tempo, and the collector have no
published ports.

## What goes where

| Signal | Source | Path | Retention set in |
| --- | --- | --- | --- |
| Traces | ASP.NET Core, HttpClient, and `StreamKeepr` spans | OTLP/gRPC → collector → Tempo | `tempo.yaml` (`block_retention`) |
| Metrics | `StreamKeepr` meter, ASP.NET Core, HttpClient, `System.Runtime` | OTLP/gRPC → collector → Prometheus OTLP receiver | `OBSERVABILITY_METRICS_RETENTION` (default in `compose.observability.yaml`) |
| Structured logs | Serilog compact-JSON file sink on the `app-logs` volume | collector `file_log` receiver → Loki | `loki.yaml` (`retention_period`) |
| Health checks | collector `http_check` probes of `/health/live` and `/health/ready` every 15 s | → Prometheus as `httpcheck_status` | as metrics |

**Logs travel as files.** Serilog is the app's only logging pipeline; OTLP log export is
deferred to #197. The overlay points `Serilog:File:Path` at `/logs`, a volume the collector
reads read-only, so the JSON files are no longer under `<recordings>/.streamkeepr/logs/`.

The collector maps each line's level to a severity and its `@tr`/`@sp` ids to the record's
trace context. Every event property becomes Loki structured metadata, so you can filter on
it, for example `{service_name="streamkeepr"} | SourceContext=~"StreamKeepr.Infrastructure.Media.*"`.
The log line body is the Serilog message template, for example
`HTTP {RequestMethod} {RequestPath} responded {StatusCode}`; the values are in the fields.

**Metrics are pushed, not scraped.** The collector pushes to Prometheus's OTLP receiver
(`--web.enable-otlp-receiver`) instead of exposing a Prometheus exporter for Prometheus to
scrape, so the collector has no metrics port. Only `service.name` becomes a label.
`service.version` stays on `target_info`, so a deploy does not start new series.

The image's own Docker `HEALTHCHECK` still runs; `docker compose ps` shows it.

## Grafana

- **Dashboards → Stream Keepr → Stream Keepr** has these panels:
  - Health: liveness and readiness (`NO DATA` when no probe result arrived in 5 minutes),
    free recording space (orange below 2 × `MinFreeGb`, red below `MinFreeGb`), Twitch poll
    age, degraded or unhealthy health checks (from logs, because `/health/ready` stays 200
    while a check is Degraded), probe latency.
  - Twitch: EventSub push coverage, connection state, reconnects, EventSub and chat
    connection events (logs).
  - Recorder: channels by DVR state, part, stall and force-kill counts, part duration and
    size percentiles, chat drops, export jobs, per-channel state changes and recorder part
    starts and finishes (logs).
  - HTTP: request rate and latency (static assets excluded), outbound HTTP calls (the
    collector exports excluded), Blazor circuits and connections.
  - .NET runtime: memory, CPU, exceptions by type.
  - Logs and traces: warnings and errors by logger, a log panel with a level filter, errored
    traces, recent traces.
  - Annotations mark each app start and stop.
- The Prometheus data source sets `timeInterval: 30s`, the app's metric export interval, so
  `$__rate_interval` always spans several samples. Change it if `OTEL_METRIC_EXPORT_INTERVAL`
  changes.
- **Explore → Loki**: expand a log line and use its **Trace** link to open the trace.
- **Explore → Tempo**: a span's **Logs for this span** link opens its log lines.

The data sources and dashboard come from `grafana/provisioning` and `grafana/dashboards`.
The dashboard is read-only in the UI: edit `grafana/dashboards/stream-keepr.json` and
Grafana reloads it within about 10 seconds.

The event counters (recorder parts, stalls and force kills, chat drops, export jobs, and
EventSub reconnects) start at 0 for every known label value once the app has started
(`StreamKeeprTelemetry.InitializeCounters`). `increase()` ignores a new series' first sample,
so without that zero the first event after a start would not show. Their panels read 0 until
something happens. The part duration and size percentiles have no zero to start from: they
stay empty until the second part of each exit kind finishes after a start. The "Recorder parts"
log panel lists every part regardless.

## Resources

Each long-running service has a `mem_limit` in `compose.observability.yaml`, and the
collector's `memory_limiter` refuses data before it reaches its limit. The limits come from
`docker stats` on an idle stack plus headroom. Raise them, and the collector's
`limit_mib` with its `mem_limit`, if a service is OOM-killed (`docker compose ps` shows
exit code 137).

## If something is down

- **Collector down:** the app keeps running and serving. Its exports time out after 3 s and
  are dropped. Log lines are not lost: the collector resumes the files from its saved
  offsets in `otel-collector-data`.
- **`volume-init` failed:** the app does not start, because an unwritable log directory fails
  startup. `docker compose logs volume-init` shows why.

## Files

| File | Purpose |
| --- | --- |
| `otel-collector.yaml` | Receivers (OTLP, log files, health probes), memory limiter, exporters |
| `prometheus.yml` | OTLP resource-attribute promotion, out-of-order window |
| `loki.yaml` | Single-binary Loki, filesystem storage, retention |
| `tempo.yaml` | Single-binary Tempo 3, local storage, retention |
| `grafana/` | Data sources (with trace and log links) and the dashboard |

The `volume-init` service runs once per `up`. It hands the fresh `app-logs`, `tempo-data`,
and `otel-collector-data` volumes to the users those containers run as: 1654 for the app,
and 10001 for Tempo and the collector.

The service name `streamkeepr` appears in `otel-collector.yaml`, the Grafana Tempo data
source, and the dashboard's hidden `service` variable. It is `StreamKeeprTelemetry.ServiceName`,
so change them together.

## Stopping and resetting

```bash
docker compose -f compose.yaml -f compose.observability.yaml down
```

`down -v` also deletes the volumes. That removes the **recordings volume as well** as the
telemetry data. To clear only telemetry, remove the stack's `app-logs`, `otel-collector-data`,
`tempo-data`, `loki-data`, `prometheus-data`, and `grafana-data` volumes by name.

## Differences from the research memo

[`docs/research/opentelemetry.md`](../docs/research/opentelemetry.md) §7 is advisory. This
overlay differs from it on purpose in four places:

- **No `observability` profile.** Passing the second `-f` file already opts in.
- **Metrics are pushed, not scraped.** OTLP push instead of a collector Prometheus exporter
  on `:8889`.
- **Loki, log-file tailing, and health probes are added.** The deployment request asked for
  structured logs and health checks as well as metrics and traces.
- **No `mem_limit` values from the memo.** The memo's resource use was unknown; these limits
  were measured.
