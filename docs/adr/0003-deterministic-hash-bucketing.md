# ADR-0003: Deterministic hash bucketing instead of stored assignments

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

An experiment must give the same user the same variation every time, on every server, in every SDK, without a network call on the hot path. We must also be able to explain and test the assignment logic.

## Considered options

1. Store each user's assignment in a database or cache and look it up per decision.
2. Assign randomly on first sight and persist in a cookie or client storage.
3. Compute the assignment as a pure function of `(bucketing id, experiment key)` using a stable hash.

## Decision outcome

Chosen: **option 3**.

- Hash: MurmurHash3 x86 32-bit, seed `1`, over the UTF-8 bytes of `bucketingId + 0x1F + experimentKey`. The unit-separator byte prevents ambiguity such as `("ab","c")` colliding with `("a","bc")`.
- Bucket value: `floor(hash / 2^32 * 10_000)`, an integer in `[0, 9999]`.
- Traffic allocation: each variation owns a contiguous range of the bucket space, expressed in basis points (1 basis point = 0.01%). Ranges are laid out in variation order. Unallocated buckets mean "not in the experiment".
- Using the experiment key as part of the hash input decorrelates assignments across experiments.
- The implementation is checked against published MurmurHash3 test vectors and against distribution tests (see `Splitlab.Sdk.Tests`).

## Consequences

- Positive: no storage, no network and no coordination are needed; the decision is reproducible in any language that implements the same hash.
- Positive: easy to test exhaustively and to reason about.
- Negative: changing allocations can move users between variations. Rule: when traffic grows, extend ranges at the end of the bucket space rather than reordering variations. Variation order is part of the experiment's contract.
- Negative: no per-user overrides or "sticky" behaviour after changes. If needed, a persisted override layer can sit in front of this function in a later ADR.
