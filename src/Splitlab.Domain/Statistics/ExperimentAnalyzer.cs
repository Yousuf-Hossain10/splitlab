namespace Splitlab.Domain.Statistics;

/// <summary>Observed data for one variation. The first variation passed to the analyzer is the control.</summary>
/// <param name="AllocationBasisPoints">Configured traffic share, used by the sample ratio check.</param>
/// <param name="Exposed">Users who saw this variation (each counted once, multi-variation users already removed).</param>
/// <param name="Converted">Of those, users who converted.</param>
public sealed record VariationData(string Key, int AllocationBasisPoints, long Exposed, long Converted);

public sealed class AnalysisOptions
{
    /// <summary>Beta prior on the conversion rate. Beta(1, 1) is uniform: no opinion before seeing data.</summary>
    public double PriorAlpha { get; init; } = 1;

    public double PriorBeta { get; init; } = 1;

    /// <summary>Width of the credible interval.</summary>
    public double CredibleLevel { get; init; } = 0.95;

    /// <summary>Monte Carlo draws for probability-to-beat-control and expected loss.</summary>
    public int MonteCarloSamples { get; init; } = 100_000;

    /// <summary>Fixed seed, so the same data always gives the same numbers.</summary>
    public ulong Seed { get; init; } = 20_261_019;

    /// <summary>
    /// Expected loss (in absolute conversion rate, so 0.001 = 0.1 percentage points) below which a variation is
    /// called the leader: choosing it risks giving up less than this on average.
    /// </summary>
    public double ExpectedLossThreshold { get; init; } = 0.001;

    /// <summary>No recommendation is made until every variation has at least this many exposed users.</summary>
    public long MinExposuresPerVariation { get; init; } = 100;

    /// <summary>Sample ratio mismatch p-value threshold.</summary>
    public double SrmThreshold { get; init; } = SampleRatioMismatch.DefaultThreshold;
}

/// <param name="ObservedRate">Converted / exposed, as a plain ratio.</param>
/// <param name="PosteriorMean">Mean of the conversion rate's posterior, which shrinks slightly toward the prior for small samples.</param>
/// <param name="ProbabilityToBeatControl">P(this variation's true rate &gt; control's); null for the control itself.</param>
/// <param name="ExpectedLoss">Average amount of conversion rate given up by choosing this variation, relative to the best variation in each possible world.</param>
/// <param name="ProbabilityBest">P(this variation has the highest true rate).</param>
public sealed record VariationResult(
    string Key,
    bool IsControl,
    long Exposed,
    long Converted,
    double ObservedRate,
    double PosteriorMean,
    double CredibleLow,
    double CredibleHigh,
    double? ProbabilityToBeatControl,
    double ExpectedLoss,
    double ProbabilityBest);

public enum RecommendationKind
{
    /// <summary>The data failed a quality check; no winner is shown.</summary>
    Untrustworthy,

    /// <summary>Some variation has too few users to say anything yet.</summary>
    KeepCollecting,

    /// <summary>One variation's expected loss is below the threshold.</summary>
    Leader,

    /// <summary>Enough data, but no variation is clearly ahead.</summary>
    Inconclusive,
}

public sealed record Recommendation(RecommendationKind Kind, string? Variation, string Message);

public sealed record ExperimentAnalysis(
    IReadOnlyList<VariationResult> Variations,
    SampleRatioMismatchResult? SampleRatio,
    Recommendation Recommendation,
    int MonteCarloSamples);

/// <summary>
/// Bayesian analysis of a binary conversion metric (ADR-0006): a Beta-Binomial model per variation, with a
/// sample ratio check that overrides any recommendation when the data looks broken.
/// </summary>
public static class ExperimentAnalyzer
{
    public static ExperimentAnalysis Analyze(IReadOnlyList<VariationData> variations, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();
        Validate(variations, options);

        var posteriors = variations
            .Select(v => new BetaDistribution(options.PriorAlpha + v.Converted, options.PriorBeta + (v.Exposed - v.Converted)))
            .ToArray();

        (double[] beatControl, double[] expectedLoss, double[] best) = Simulate(posteriors, options);

        double tail = (1 - options.CredibleLevel) / 2;
        var results = new List<VariationResult>(variations.Count);
        for (int i = 0; i < variations.Count; i++)
        {
            VariationData v = variations[i];
            results.Add(new VariationResult(
                v.Key,
                IsControl: i == 0,
                v.Exposed,
                v.Converted,
                ObservedRate: v.Exposed == 0 ? 0 : (double)v.Converted / v.Exposed,
                PosteriorMean: posteriors[i].Mean,
                CredibleLow: posteriors[i].Quantile(tail),
                CredibleHigh: posteriors[i].Quantile(1 - tail),
                ProbabilityToBeatControl: i == 0 ? null : beatControl[i],
                ExpectedLoss: expectedLoss[i],
                ProbabilityBest: best[i]));
        }

        SampleRatioMismatchResult? srm = SampleRatioMismatch.Test(
            variations.Select(v => (v.Exposed, v.AllocationBasisPoints)).ToList(), options.SrmThreshold);

        return new ExperimentAnalysis(results, srm, Recommend(results, srm, options), options.MonteCarloSamples);
    }

    /// <summary>
    /// Draws all variations' conversion rates together, many times, so comparisons between them are joint.
    /// Returns, per variation: P(beats control), expected loss against the best variation, and P(best).
    /// </summary>
    private static (double[] BeatControl, double[] ExpectedLoss, double[] Best) Simulate(
        BetaDistribution[] posteriors, AnalysisOptions options)
    {
        int k = posteriors.Length;
        var random = new SeededRandom(options.Seed);
        var draw = new double[k];
        var beat = new long[k];
        var best = new long[k];
        var loss = new double[k];

        for (int s = 0; s < options.MonteCarloSamples; s++)
        {
            int bestIndex = 0;
            for (int i = 0; i < k; i++)
            {
                draw[i] = posteriors[i].Sample(random);
                if (draw[i] > draw[bestIndex])
                {
                    bestIndex = i;
                }
            }

            best[bestIndex]++;
            for (int i = 0; i < k; i++)
            {
                loss[i] += draw[bestIndex] - draw[i];
                if (draw[i] > draw[0])
                {
                    beat[i]++;
                }
            }
        }

        double n = options.MonteCarloSamples;
        return (
            beat.Select(c => c / n).ToArray(),
            loss.Select(l => l / n).ToArray(),
            best.Select(c => c / n).ToArray());
    }

    private static Recommendation Recommend(
        List<VariationResult> results, SampleRatioMismatchResult? srm, AnalysisOptions options)
    {
        if (srm is { Mismatch: true })
        {
            return new Recommendation(
                RecommendationKind.Untrustworthy,
                null,
                $"Sample ratio mismatch (p = {srm.PValue:G3}): traffic is not split as configured, so these results cannot be trusted. Check assignment and event logging before drawing conclusions.");
        }

        long smallest = results.Min(r => r.Exposed);
        if (smallest < options.MinExposuresPerVariation)
        {
            return new Recommendation(
                RecommendationKind.KeepCollecting,
                null,
                $"Not enough data yet: the smallest variation has {smallest} exposed users and at least {options.MinExposuresPerVariation} are needed.");
        }

        VariationResult leader = results.OrderBy(r => r.ExpectedLoss).First();
        return leader.ExpectedLoss < options.ExpectedLossThreshold
            ? new Recommendation(
                RecommendationKind.Leader,
                leader.Key,
                $"'{leader.Key}' is the leader: choosing it is expected to give up {leader.ExpectedLoss:P3} of conversion rate, below the {options.ExpectedLossThreshold:P3} threshold.")
            : new Recommendation(
                RecommendationKind.Inconclusive,
                null,
                "No variation is clearly ahead yet; the lowest expected loss is still above the threshold.");
    }

    private static void Validate(IReadOnlyList<VariationData> variations, AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(variations);
        if (variations.Count < 2)
        {
            throw new ArgumentException("At least two variations (a control and one other) are needed.", nameof(variations));
        }

        if (variations.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count() != variations.Count)
        {
            throw new ArgumentException("Variation keys must be unique.", nameof(variations));
        }

        foreach (VariationData v in variations)
        {
            if (v.Exposed < 0 || v.Converted < 0 || v.Converted > v.Exposed)
            {
                throw new ArgumentException($"Variation '{v.Key}' has impossible counts ({v.Converted} conversions of {v.Exposed} exposed).", nameof(variations));
            }
        }

        if (options.PriorAlpha <= 0 || options.PriorBeta <= 0)
        {
            throw new ArgumentException("Prior parameters must be positive.", nameof(options));
        }

        if (options.CredibleLevel is <= 0 or >= 1 || options.MonteCarloSamples < 1)
        {
            throw new ArgumentException("Invalid credible level or sample count.", nameof(options));
        }
    }
}
