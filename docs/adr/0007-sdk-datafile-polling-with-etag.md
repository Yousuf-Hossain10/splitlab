# ADR-0007: SDK configuration delivery by polling a datafile with ETag

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

SDK decisions must be fast and must keep working when the Splitlab API is slow or down. The SDK therefore needs a local copy of the experiment configuration, and a way to keep it fresh.

## Considered options

1. Call the API for every decision.
2. Poll a JSON datafile per environment and decide locally.
3. Hold a server-sent-events or WebSocket connection and push changes.

## Decision outcome

Chosen: **option 2**.

- `GET /v1/config/{environmentKey}` returns a versioned JSON datafile (flags, experiments, variations, allocations) with a strong `ETag`.
- The SDK polls with `If-None-Match` every 30 seconds by default, with random jitter, and treats `304 Not Modified` as success.
- The current configuration is an immutable snapshot swapped atomically (`Volatile`/`Interlocked`), so decisions never see a half-updated state and need no locks.
- On fetch failure the SDK keeps the last known good snapshot and retries with exponential backoff. If it has never loaded a configuration, decisions return the caller-supplied default value and report the reason.
- An optional embedded datafile can bootstrap the SDK at start-up.
- The datafile endpoint is cacheable by a CDN because it is a pure function of environment and version.

## Consequences

- Positive: zero network calls and no I/O on the decision path; resilient to outages.
- Negative: configuration changes reach SDKs after up to one polling interval. Acceptable for experiments; a streaming option can be added later without changing the decision logic.
- Negative: the datafile format becomes a public contract and needs a version field and compatibility tests.
