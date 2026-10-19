# ADR-0011: Experiment insights from a language model, isolated behind an interface

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

Result tables are accurate but hard for non-statisticians to act on. A short plain-English explanation of an experiment's outcome and a suggested next test would help. A language model can produce that, but it adds latency, cost, a third-party dependency and the risk of wrong or misleading text.

## Considered options

1. No generated text.
2. Call a model provider directly from the request handlers.
3. Define an application-level interface, implement it with a provider adapter in Infrastructure, and treat the output as an optional, clearly labelled aid.

## Decision outcome

Chosen: **option 3**.

- Interface: `IExperimentInsightsGenerator.GenerateAsync(ExperimentResultSummary, CancellationToken)` in Application. The provider adapter lives in Infrastructure and is selected by configuration, so the provider can be changed without touching use cases.
- Input is limited to aggregated results (counts, rates, intervals, health flags). No user identifiers or raw events are sent.
- The numbers shown to the user always come from the statistics engine. The model only writes narrative around them, and the prompt includes the computed figures verbatim. A test asserts that narrative requests contain the computed values.
- Output is labelled "AI-generated" in the dashboard and is never used to make or change a decision automatically.
- Resilience: request timeout, bounded retries and a circuit breaker; failures degrade to "insights unavailable" without affecting the results page.
- Responses are cached by a hash of the input summary to avoid repeat cost.
- The feature is disabled when no provider is configured, and tests use a fake implementation.

## Consequences

- Positive: a useful, low-risk feature that is easy to test, replace or remove.
- Negative: ongoing cost and a dependency on an external service; mitigated by caching and by the feature being optional.
- Negative: generated narrative can be imperfect; mitigated by labelling and by keeping decisions with the human reader.
