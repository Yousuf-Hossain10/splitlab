# ADR-0005: Event ingestion through a bounded in-process queue with batched writes

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

SDKs report exposure and conversion events. Volume is bursty and far higher than configuration traffic. The API must acknowledge quickly, must not let a slow database stall request threads, and must tolerate retries from clients.

## Considered options

1. Insert each event synchronously inside the HTTP request.
2. Accept events into a bounded in-process queue (`System.Threading.Channels`) and write them in batches from a hosted service.
3. Publish to a message broker (RabbitMQ, Kafka) and consume with a separate worker.

## Decision outcome

Chosen: **option 2**, with a seam that allows option 3 later.

- `POST /v1/events` accepts a batch (maximum 500 events), validates it, writes accepted events to a bounded `Channel<EventEnvelope>` and returns `202 Accepted`.
- A `BackgroundService` drains the channel and flushes when it holds 500 events or 1 second has passed, whichever comes first, using bulk insert.
- Idempotency: events carry a client-generated `eventId`. The insert uses `ON CONFLICT DO NOTHING` on `(environment_id, event_id)`, so SDK retries are safe. Delivery is at-least-once, effectively once after deduplication.
- Backpressure: when the channel is full the endpoint returns `429 Too Many Requests` with `Retry-After`, and SDKs back off with jitter.
- The producer side is behind an `IEventSink` interface in Application, so a broker-backed implementation can replace the channel without changing the endpoint.
- Graceful shutdown drains the channel before the host stops, within the shutdown timeout.

## Consequences

- Positive: low and predictable request latency; database writes are efficient; retries are harmless.
- Negative: events accepted but not yet flushed are lost if the process crashes. The window is bounded by the batch size and flush interval. This is acceptable for analytics counts and is documented to users; a durable outbox or broker is the follow-up if stronger guarantees are needed.
- Negative: queue state is per-instance, so queue depth must be monitored per instance (ADR-0009).

## Implementation notes

- The deduplication key is `(environment_id, event_id, occurred_at)`, not `(environment_id, event_id)`. PostgreSQL requires a unique key on a partitioned table to contain the partition column, and the table is to be partitioned by `occurred_at` (ADR-0004). SDKs must therefore resend a retried event unchanged, including its timestamp, which is why `timestamp` is required. Timestamps are truncated to milliseconds.
- The bounded queue accepts a batch whole or not at all (room is reserved up front), so a `429` never leaves a half-accepted batch behind.
- The events table currently has no partitions and no foreign keys; partitioning and a retention job are follow-up work.
