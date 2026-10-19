# ADR-0006: Bayesian primary analysis with a Sample Ratio Mismatch check

- Status: Accepted
- Date: 2026-10-19

## Context and problem statement

People look at experiment results while the experiment is still running. A fixed-horizon significance test used that way produces many false positives. Separately, a broken assignment or logging pipeline can skew the traffic split, which makes every downstream number untrustworthy.

## Considered options

1. Fixed-horizon frequentist test (two-proportion z-test) with a pre-set sample size.
2. Sequential frequentist testing (always-valid p-values).
3. Bayesian analysis with a Beta-Binomial model for conversion rate.

## Decision outcome

Chosen: **option 3** as the primary analysis, plus a mandatory data-quality check.

- Model: for each variation, conversions out of exposures with a `Beta(1, 1)` prior. Reported per variation: posterior mean conversion rate, a 95% credible interval, probability to beat control, and expected loss of choosing that variation.
- Probability to beat control is computed by Monte Carlo sampling with a fixed seed, so results are reproducible and testable. A closed-form or numerical-integration check is used in tests for the two-variation case.
- Decision guidance uses expected loss against a configurable threshold, not only probability to beat control.
- Sample Ratio Mismatch check: a chi-square goodness-of-fit test of observed exposures against the configured allocation. A p-value below `0.001` marks the experiment as unhealthy and the dashboard shows a prominent warning instead of a winner.
- Honest limits: Bayesian monitoring tolerates repeated looks better than a fixed-horizon test but is not immune to all stopping-rule effects. The UI states this and shows sample sizes alongside every probability.
- Users in more than one variation of the same experiment are excluded and counted separately.

## Consequences

- Positive: results are easier to read, and the SRM check catches the most common cause of bad experiments.
- Negative: more statistical code to maintain and to test carefully; a frequentist view may be added later as a secondary tab (new ADR).
- Negative: only binary conversion metrics are covered at first; continuous metrics need a different model.
