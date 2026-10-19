# ADR-0010: Separate authentication for people and for SDKs; tenant isolation by project

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

Two kinds of caller use Splitlab: people managing experiments in the dashboard, and applications embedding the SDK. They have different threat models. Multiple teams (projects) share one deployment and must not see each other's data.

## Considered options

1. One mechanism (API keys or JWTs) for both callers.
2. JWT bearer tokens for people and environment-scoped SDK keys for machines.
3. Delegating everything to an external identity provider, including machine access.

## Decision outcome

Chosen: **option 2**.

- Management API: JWT bearer authentication validated against a configurable OpenID Connect authority. Development uses a local issuer so no external service is required. Roles per project: `Viewer`, `Editor`, `Admin`, enforced by authorization policies.
- SDK access: keys scoped to one environment with explicit permissions (`config:read`, `events:write`). Keys are shown once on creation, stored only as a SHA-256 hash plus a short non-secret prefix for identification, and can be rotated or revoked.
- Tenant isolation: every tenant-owned row carries `project_id`; EF Core global query filters apply it; integration tests assert that a caller from project A cannot read or write project B's data on every endpoint.
- Rate limiting per SDK key on event ingestion, using the built-in ASP.NET Core rate limiter.

## Consequences

- Positive: least privilege for SDK keys, which live in client-side applications and will eventually leak; a leaked key can only read config and write events for one environment.
- Negative: two authentication schemes to maintain and test.
- Negative: SDK keys in browser-based apps are public by nature, so the events endpoint must be tolerant of abuse (validation, rate limits, per-key quotas).
