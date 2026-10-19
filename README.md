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
| Decision SDK: datafile client, polling, decide API | Planned |
| Management API, persistence | Planned |
| Event ingestion pipeline | Planned |
| Statistics engine | Planned |
| Angular dashboard | Planned |
| Observability, load tests | Planned |

## Repository layout

```
src/Splitlab.Sdk/           Decision SDK (no server dependencies)
tests/Splitlab.Sdk.Tests/   SDK unit tests
docs/adr/                   Architecture decision records
.github/workflows/          CI
```

## Getting started

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build
dotnet test
```

## Bucketing in one example

```csharp
using Splitlab.Sdk.Bucketing;

var experiment = new Experiment(
    "checkout_flow",
    [new Variation("control", 5_000), new Variation("treatment", 5_000)]);

// Same user + same experiment => same answer, on any machine.
string? variation = Bucketer.Assign("user-42", experiment);
```

`Assign` returns `null` when a user falls outside the experiment's allocated traffic. The hash input, bucket space and range layout are specified in [ADR-0003](docs/adr/0003-deterministic-hash-bucketing.md), and golden test vectors in `BucketerTests` let other language SDKs verify they agree.
