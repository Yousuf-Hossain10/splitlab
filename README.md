# Splitlab

Feature flags and A/B experimentation platform: a .NET 8 API, a decision SDK, an event pipeline, a statistics engine and an Angular dashboard.

Splitlab is a portfolio project that explores how experimentation platforms work: deterministic user bucketing, fast local SDK decisions, a resilient event pipeline and trustworthy results. Every significant design choice is recorded in [`docs/adr`](docs/adr/README.md).

## How it works

```mermaid
flowchart LR
    App["Your application<br/>+ Splitlab SDK"] -- "poll datafile (ETag)" --> Config["Config endpoint"]
    App -- "exposure / conversion events" --> Ingest["Ingestion endpoint"]
    Ingest --> Queue["Bounded queue"] --> Writer["Batch writer"] --> DB[("PostgreSQL")]
    Dashboard["Angular dashboard"] --> Mgmt["Management API"] --> DB
    Rollups["Rollup job + stats engine"] --> DB
    Dashboard --> Results["Results API"] --> Rollups
```

- **Decisions are local.** The SDK downloads a versioned datafile and assigns users itself, with no network call per decision ([ADR-0003](docs/adr/0003-deterministic-hash-bucketing.md), [ADR-0007](docs/adr/0007-sdk-datafile-polling-with-etag.md)).
- **Events are queued, then written in batches** and deduplicated by client event id ([ADR-0005](docs/adr/0005-event-ingestion-with-bounded-channel.md)).
- **Results are Bayesian**, with a sample ratio mismatch check that flags broken experiments ([ADR-0006](docs/adr/0006-bayesian-stats-with-srm-check.md)).

## Status

| Area | State |
| --- | --- |
| Decision SDK: hashing and bucketing | Done, tested |
| Decision SDK: datafile client, polling, decide API | Done, tested |
| Management API, persistence | Done: projects, environments, experiments, audit log, datafile endpoint. Feature flags still to do |
| Authentication, tenant isolation | Done, tested: JWT roles per project, hashed SDK keys, query filters. Rate limiting arrives with event ingestion |
| Event ingestion pipeline | Done, tested on SQLite; the PostgreSQL `COPY` path is not yet exercised. SDK-side event client still to do |
| Statistics engine | Done, tested: Bayesian results and sample ratio check |
| Rollup tables | Done, tested on SQLite: per-user state kept in step with events; the PostgreSQL upsert is not yet exercised |
| Angular dashboard | Done for projects, experiments, results and SDK keys, tested (unit and component); sign-in through an identity provider, flags and end-to-end tests still to do |
| Observability, load tests | Planned |

## Repository layout

```
src/Splitlab.Sdk/            Decision SDK (no server dependencies)
src/Splitlab.Domain/         Entities and business rules (no dependencies)
src/Splitlab.Application/    Use cases and repository interfaces
src/Splitlab.Infrastructure/ EF Core, PostgreSQL, migrations
src/Splitlab.Api/            HTTP endpoints and composition root
dashboard/                   Angular dashboard (generated API client; see dashboard/README.md)
tests/                       SDK, domain and API integration tests, and the end-to-end test host
docs/adr/                   Architecture decision records
.github/workflows/          CI
```

## Getting started

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build
dotnet test
```

To run the API locally (migrations apply automatically in Development):

```bash
docker compose up -d
dotnet run --project src/Splitlab.Api
```

The management API lives under `/v1/projects/...` and the SDK polls `GET /v1/config/{environmentKey}`.

### The dashboard

`dashboard/` is an Angular app for projects, experiments, results and SDK keys. Its TypeScript client is generated from the API's OpenAPI document, which is committed at `dashboard/openapi/splitlab.v1.json` and checked by a test, so API changes cannot silently break it. See [dashboard/README.md](dashboard/README.md).

### Sending events ([ADR-0005](docs/adr/0005-event-ingestion-with-bounded-channel.md))

`POST /v1/events` with an SDK key that has `events:write`. Up to 500 events per request; every event needs an `eventId`, a `userId` and a `timestamp` (a retry must resend the same timestamp so it is recognised as a duplicate):

```json
{ "events": [
  { "eventId": "7b1c...", "type": "exposure", "experimentKey": "checkout_flow", "variationKey": "treatment", "userId": "user-42", "timestamp": "2026-10-20T12:00:00Z" },
  { "eventId": "9d2e...", "type": "conversion", "name": "purchase", "value": 19.99, "userId": "user-42", "timestamp": "2026-10-20T12:00:07Z" }
] }
```

`202` means the valid events were queued (invalid ones are listed by index in `rejected`). `429` with `Retry-After` means back off and retry the same batch. Events are written in batches, so a crash can lose the few seconds still in memory.

### Results ([ADR-0006](docs/adr/0006-bayesian-stats-with-srm-check.md))

`GET /v1/projects/{p}/environments/{e}/experiments/{key}/results?metric=purchase` (any member) returns, per variation, the exposed and converted users, the observed rate, the posterior mean with a 95% credible interval, the probability to beat control and the expected loss. It also reports the sample ratio check and a recommendation: `Leader`, `Inconclusive`, `KeepCollecting`, or `Untrustworthy` when the traffic split does not match the configuration (in which case no winner is shown). Users seen in more than one variation are excluded and counted separately. Output is deterministic for a given dataset. Results are read from per-user rollup tables that are updated in the same transaction as the events, so they are always current; `POST /v1/projects/{p}/environments/{e}/rollups/rebuild` (Admin) recomputes them from the raw events, which is needed once after upgrading a deployment that already holds events.

### Authentication ([ADR-0010](docs/adr/0010-authentication-and-tenant-isolation.md))

- **People** send a JWT bearer token. In production set `Auth:Authority` and `Auth:Audience` to your OpenID Connect provider. In Development a local issuer is enabled, so you can get a token without an identity provider:

  ```bash
  curl -s -X POST localhost:5000/dev/token -H "Content-Type: application/json" -d '{"subject":"alice"}'
  ```

- Whoever creates a project becomes its Admin and can add members as `Viewer`, `Editor` or `Admin` (`PUT /v1/projects/{p}/members/{subject}`). Callers outside a project get 403, the same as for a project that does not exist.
- **SDKs** use environment-scoped keys (`POST /v1/projects/{p}/environments/{e}/api-keys`). The secret is returned once; only a SHA-256 hash and a short prefix are stored. Keys can be revoked or rotated.
- Every tenant-owned table carries `project_id`. EF Core query filters scope reads, and a write guard rejects saving into another project. With no tenant set, queries return nothing.

## Bucketing in one example

```csharp
using Splitlab.Sdk.Bucketing;

var experiment = new Experiment(
    "checkout_flow",
    [new Variation("control", 5_000), new Variation("treatment", 5_000)]);

// Same user + same experiment => same answer, on any machine.
string? variation = Bucketer.Assign("user-42", experiment);
```

With a running API, `SplitlabClient` polls the datafile (ETag, jitter, exponential backoff) and decides locally; it keeps the last good snapshot on failure and returns your default plus a reason when it has nothing to go on ([ADR-0007](docs/adr/0007-sdk-datafile-polling-with-etag.md)):

```csharp
using var client = new SplitlabClient(
    new HttpDatafileSource(new HttpClient(), new Uri("https://splitlab.example/"), "production", sdkKey: "sl_..."));
client.Start();

Decision d = client.Decide("checkout_flow", "user-42", defaultVariation: "control");
// d.VariationKey, d.Reason (Assigned, NotInExperiment, ExperimentNotFound, ConfigNotLoaded)
```

`Assign` returns `null` when a user falls outside the experiment's allocated traffic. The hash input, bucket space and range layout are specified in [ADR-0003](docs/adr/0003-deterministic-hash-bucketing.md), and golden test vectors in `BucketerTests` let other language SDKs verify they agree.
