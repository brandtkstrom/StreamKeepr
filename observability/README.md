# Observability stack

`compose.observability.yaml` at the repository root layers an OpenTelemetry Collector,
Prometheus, Loki, Tempo, and Grafana onto the regular build-from-checkout deployment in
`compose.yaml`. The base file keeps every production setting (secrets, volumes, ports);
the overlay only switches telemetry on and adds the observability services.

```bash
docker compose -f compose.yaml -f compose.observability.yaml up -d --build
```

To skip the `-f` flags, put `COMPOSE_FILE=compose.yaml:compose.observability.yaml` in `.env`.
The Twitch secret files under `./secrets` are required as usual (see the main README).

| What | Where |
| --- | --- |
| Stream Keepr | http://localhost:8080 (`STREAMKEEP_PORT`) |
| Grafana | http://localhost:3000 (`GRAFANA_PORT`). Sign in as `admin` / `admin`, then set a new password when asked. |
| Prometheus | http://localhost:9090 (`PROMETHEUS_PORT`) |

Grafana and Prometheus listen on 127.0.0.1 only. Loki, Tempo, and the collector have no
published ports.

## What goes where

| Signal | Source | Path | Store |
| --- | --- | --- | --- |
| Traces | ASP.NET Core, HttpClient, and `StreamKeepr` spans | OTLP gRPC → collector | Tempo, 7 days |
| Metrics | `StreamKeepr` meter, ASP.NET Core, HttpClient, `System.Runtime` | OTLP gRPC → collector → Prometheus OTLP receiver | Prometheus, 15 days (`OBSERVABILITY_METRICS_RETENTION`) |
| Structured logs | Serilog compact-JSON file sink on the `app-logs` volume | collector `file_log` receiver | Loki, 7 days |
| Health checks | collector `http_check` probes of `/health/live` and `/health/ready` every 15 s | → Prometheus as `httpcheck_status` | Prometheus |

Serilog is the app's only logging pipeline, so logs travel as files instead of OTLP. The
overlay points `Serilog:File:Path` at `/logs`, a volume shared read-only with the collector.
The collector maps each line's level to severity and its `@tr`/`@sp` ids to the log record's
trace context. Every event property becomes Loki structured metadata, so you can filter on
it, for example `{service_name="streamkeepr"} | SourceContext=~"StreamKeepr.Infrastructure.Media.*"`.
The log line body is the Serilog message template, for example
`HTTP {RequestMethod} {RequestPath} responded {StatusCode}`; the values are in the fields.

The image's own Docker `HEALTHCHECK` still runs; `docker compose ps` shows it.

## Grafana

- **Dashboards → Stream Keepr → Stream Keepr**: liveness and readiness, free recording
  space, Twitch poll age, channels by DVR state, recorder events, HTTP rate and latency,
  Twitch API calls, .NET memory, warnings and errors, a log panel with a level filter, and
  recent traces.
- **Explore → Loki**: expand a log line and use its **Trace** link to open the trace.
- **Explore → Tempo**: a span's **Logs for this span** link opens its log lines.

The data sources and dashboard come from `grafana/provisioning` and `grafana/dashboards`.
The dashboard is read-only in the UI: edit `grafana/dashboards/stream-keepr.json` and
Grafana reloads it within about 10 seconds.

Recorder panels stay empty until a recording starts, because their counters have no
series until the first event.

## Files

| File | Purpose |
| --- | --- |
| `otel-collector.yaml` | Receivers (OTLP, log files, health probes) and exporters |
| `prometheus.yml` | OTLP resource-attribute promotion, out-of-order window |
| `loki.yaml` | Single-binary Loki, filesystem storage, retention |
| `tempo.yaml` | Single-binary Tempo, local storage, retention |
| `grafana/` | Data sources (with trace and log links) and the dashboard |

The `volume-init` service runs once per `up` to hand the fresh `app-logs`, `tempo-data`,
and `otel-collector-data` volumes to the users those containers run as (1654 for the app,
10001 for Tempo and the collector).

## Stopping and resetting

```bash
docker compose -f compose.yaml -f compose.observability.yaml down
```

`down -v` also deletes the volumes, which removes the **recordings volume as well** as the
telemetry data. To clear only telemetry, remove the stack's `app-logs`, `otel-collector-data`,
`tempo-data`, `loki-data`, `prometheus-data`, and `grafana-data` volumes by name.
