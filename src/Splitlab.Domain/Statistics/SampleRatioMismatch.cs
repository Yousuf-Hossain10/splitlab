namespace Splitlab.Domain.Statistics;

/// <param name="ChiSquare">The chi-square statistic; infinite when users were seen in a variation that gets no traffic.</param>
/// <param name="PValue">Probability of a split at least this uneven if assignment really followed the configured allocation.</param>
/// <param name="Mismatch">The p-value is below the threshold: the data should not be trusted.</param>
public sealed record SampleRatioMismatchResult(double ChiSquare, int DegreesOfFreedom, double PValue, bool Mismatch);

/// <summary>
/// Sample Ratio Mismatch check (ADR-0006): a chi-square goodness-of-fit test of observed exposures against the
/// configured allocation. A broken assignment or logging pipeline shows up here before it can fool anyone.
/// </summary>
public static class SampleRatioMismatch
{
    /// <summary>Default p-value below which an experiment is flagged. Deliberately strict, so false alarms are rare.</summary>
    public const double DefaultThreshold = 0.001;

    /// <param name="groups">Observed exposed users and configured traffic share (basis points) for each variation.</param>
    /// <param name="threshold">Flag a mismatch when the p-value is below this.</param>
    /// <returns>The result, or <c>null</c> when there is nothing to test (fewer than two variations with traffic, or no exposures).</returns>
    public static SampleRatioMismatchResult? Test(IReadOnlyList<(long Observed, int AllocationBasisPoints)> groups, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(groups);

        // Users in a variation that should receive no traffic cannot be explained by chance.
        if (groups.Any(g => g.AllocationBasisPoints == 0 && g.Observed > 0))
        {
            return new SampleRatioMismatchResult(double.PositiveInfinity, Math.Max(groups.Count(g => g.AllocationBasisPoints > 0) - 1, 1), 0, true);
        }

        var allocated = groups.Where(g => g.AllocationBasisPoints > 0).ToList();
        long total = allocated.Sum(g => g.Observed);
        if (allocated.Count < 2 || total == 0)
        {
            return null;
        }

        double totalAllocation = allocated.Sum(g => (double)g.AllocationBasisPoints);
        double chiSquare = 0;
        foreach ((long observed, int allocation) in allocated)
        {
            double expected = total * allocation / totalAllocation;
            double diff = observed - expected;
            chiSquare += diff * diff / expected;
        }

        int degreesOfFreedom = allocated.Count - 1;
        double p = SpecialFunctions.ChiSquareSurvival(chiSquare, degreesOfFreedom);
        return new SampleRatioMismatchResult(chiSquare, degreesOfFreedom, p, p < threshold);
    }
}
