# ADR-0001: Record architecture decisions

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

Splitlab has several design choices that are easy to forget and expensive to revisit: how users are bucketed, how events are ingested, how results are computed. Without a written record, the reasoning behind each choice is lost and the same debates repeat.

## Considered options

1. Keep decisions in a wiki or issue tracker.
2. Keep decisions in the repository as Markdown, one file per decision (ADRs).
3. Record nothing and rely on code comments and commit messages.

## Decision outcome

Chosen: **option 2**. Each significant decision gets a Markdown file in `docs/adr/`, named `NNNN-short-title.md`, using the structure of this document (context, options, decision, consequences).

Rules:

- An accepted ADR is not edited to change its meaning. A new ADR supersedes it, and the old one's status becomes `Superseded by ADR-NNNN`.
- A decision is "significant" if reversing it later would touch more than one module, change a public contract, or change a data format.
- ADRs are reviewed in the same pull request as the code they justify.

## Consequences

- Positive: decisions are versioned with the code, searchable and reviewable.
- Positive: new contributors can read why the system looks the way it does.
- Negative: small ongoing writing cost, and ADRs can drift out of date if superseding is skipped.
