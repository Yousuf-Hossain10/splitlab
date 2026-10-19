# ADR-0009: Observability with OpenTelemetry, Prometheus and Grafana

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

An experimentation platform is only useful if its numbers can be trusted, so the pipeline that produces them must be observable. The team also needs a way to detect and diagnose problems before users report them.

## Considered options

1. Plain logs only.
2. A vendor-specific APM agent.
3. OpenTelemetry for traces and metrics, exported over OTLP, with open-source backends, and structured logs correlated by trace id.

## Decision outcome

Chosen: **option 3**.

- The API and workers use the OpenTelemetry .NET SDK with OTLP export to a collector. Metrics go to Prometheus and are charted in Grafana; traces go to Tempo or Jaeger.
- Logs use Serilog with structured properties and the current trace id.
- Custom metrics for the pipeline:
  - `splitlab_ingest_queue_depth` (gauge)
  - `splitlab_ingest_flush_duration_seconds` (histogram)
  - `splitlab_ingest_events_total{outcome}` (counter: accepted, rejected, duplicate, dropped)
  - `splitlab_rollup_lag_seconds` (gauge: age of the newest event not yet reflected in results)
  - `splitlab_config_requests_total{status}` (counter, includes 304s)
- Service level objectives, initial targets:
  - ingestion endpoint p95 latency under 100 ms at the target load
  - rollup lag under 60 seconds for 99% of the time
  - datafile endpoint availability of 99.9%
- Health checks: `/health/live` (process) and `/health/ready` (database and queue below its high-water mark).
- A Docker Compose profile starts the collector, Prometheus and Grafana with a provisioned dashboard stored in the repository.

## Consequences

- Positive: vendor-neutral, reproducible locally, and the dashboard is code-reviewed along with the service.
- Negative: more moving parts in the local setup; mitigated by making the observability stack an optional Compose profile.
