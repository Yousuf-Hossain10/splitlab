# ADR-0002: Modular monolith with clean architecture; SDK as a separate package

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

Splitlab has a management API, an event ingestion path, a statistics engine and a client SDK. The ingestion path has very different load characteristics from the management API. The SDK is consumed by other people's applications and must stay small, stable and free of server dependencies.

## Considered options

1. Microservices from day one (management, ingestion, stats as separate deployables).
2. Single-project layered N-tier application.
3. Modular monolith with clean-architecture layering, plus a separately published SDK.

## Decision outcome

Chosen: **option 3**.

- Server solution layers: `Splitlab.Domain` (entities, rules, no dependencies), `Splitlab.Application` (use cases, abstractions), `Splitlab.Infrastructure` (EF Core, background workers, external adapters) and `Splitlab.Api` (HTTP, auth, composition root). Dependencies point inward only, enforced by project references and an architecture test.
- Modules (flags, experiments, events, results) are folders inside those layers with no cross-module calls except through application-level interfaces.
- `Splitlab.Sdk` is its own class library with no reference to any server project. It targets `net8.0` today and will multi-target `netstandard2.0` if broader reach is needed.
- The target framework is .NET 8 (LTS).
- The ingestion path can be hosted as a separate process from the same codebase later, because it depends only on Application abstractions.

## Consequences

- Positive: one deployable and one database to operate while the product is small; boundaries are explicit, so extraction later is a hosting change, not a rewrite.
- Positive: the SDK can be versioned and released independently.
- Negative: module boundaries rely on discipline and tests rather than network isolation.
- Negative: ingestion and management share a process until extraction, so a noisy ingestion burst can affect API latency. Mitigated by the bounded queue in ADR-0005.
