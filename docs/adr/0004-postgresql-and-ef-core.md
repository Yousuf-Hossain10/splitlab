# ADR-0004: PostgreSQL with EF Core for configuration and events

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

Splitlab stores two kinds of data. Configuration (projects, environments, flags, experiments, audiences) is relational, low volume and needs transactions and audit history. Events (exposures and conversions) are append-only, high volume and mostly read as aggregates.

## Considered options

1. PostgreSQL for everything, accessed through EF Core.
2. PostgreSQL for configuration and a column store (ClickHouse, TimescaleDB) for events.
3. A document database for everything.

## Decision outcome

Chosen: **option 1** for the first version.

- Configuration uses EF Core with migrations committed to the repository, optimistic concurrency (row version) on mutable aggregates, and an `audit_log` table written in the same transaction as each change.
- Events go to a table partitioned by day on the event timestamp, with a unique key on `(environment_id, event_id)` for idempotency (ADR-0005).
- Results are computed from rollup tables (counts per experiment, variation and hour) maintained by a background job, so dashboard queries do not scan raw events.
- Raw event writes use bulk insert (`COPY` through Npgsql), not EF change tracking.
- Every tenant-owned table carries `project_id`, and EF Core global query filters scope reads to the caller's project.

## Consequences

- Positive: one datastore to run, back up and reason about; transactional configuration changes.
- Positive: the repository interfaces in Application keep a later move of events to a column store contained to Infrastructure.
- Negative: PostgreSQL is not the cheapest engine for very large event volumes. Revisit when rollup lag or dashboard query time exceeds the targets in ADR-0009.
- Negative: partition management needs a scheduled job that creates upcoming partitions and drops expired ones.
