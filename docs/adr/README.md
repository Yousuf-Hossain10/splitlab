# Architecture decision records

Each file records one significant decision using a MADR-style structure: context, options considered, decision, consequences. See [ADR-0001](0001-record-architecture-decisions.md) for the rules.

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith-and-clean-architecture.md) | Modular monolith with clean architecture; SDK as a separate package | Accepted |
| [0003](0003-deterministic-hash-bucketing.md) | Deterministic hash bucketing instead of stored assignments | Accepted |
| [0004](0004-postgresql-and-ef-core.md) | PostgreSQL with EF Core for configuration and events | Accepted |
| [0005](0005-event-ingestion-with-bounded-channel.md) | Event ingestion through a bounded in-process queue with batched writes | Accepted |
| [0006](0006-bayesian-stats-with-srm-check.md) | Bayesian primary analysis with a Sample Ratio Mismatch check | Accepted |
| [0007](0007-sdk-datafile-polling-with-etag.md) | SDK configuration delivery by polling a datafile with ETag | Accepted |
| [0008](0008-angular-standalone-signals-generated-client.md) | Angular with standalone components, signals and a generated API client | Accepted |
| [0009](0009-observability-with-opentelemetry.md) | Observability with OpenTelemetry, Prometheus and Grafana | Accepted |
| [0010](0010-authentication-and-tenant-isolation.md) | Separate authentication for people and for SDKs; tenant isolation by project | Accepted |
| [0011](0011-llm-insights-behind-an-interface.md) | Experiment insights from a language model, isolated behind an interface | Accepted |
