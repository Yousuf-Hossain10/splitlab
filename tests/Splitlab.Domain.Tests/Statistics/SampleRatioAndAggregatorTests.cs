using Splitlab.Domain.Statistics;

namespace Splitlab.Domain.Tests.Statistics;

public class SampleRatioMismatchTests
{
    [Fact]
    public void AnExactSplitIsPerfectlyHealthy()
    {
        SampleRatioMismatchResult? result = SampleRatioMismatch.Test([(5000, 5000), (5000, 5000)]);

        Assert.Equal(0, result!.ChiSquare);
        Assert.Equal(1, result.PValue, 9);
        Assert.False(result.Mismatch);
    }

    [Fact]
    public void ChiSquareAndPValueMatchHandComputedValues()
    {
        // 5100 / 4900 against 50 / 50: chi2 = 2 * (100^2 / 5000) = 4, df 1, p = 0.0455.
        SampleRatioMismatchResult mild = SampleRatioMismatch.Test([(5100, 5000), (4900, 5000)])!;
        // 5200 / 4800: chi2 = 16, p = 6.33e-5.
        SampleRatioMismatchResult bad = SampleRatioMismatch.Test([(5200, 5000), (4800, 5000)])!;

        Assert.Equal(4, mild.ChiSquare, 9);
        Assert.Equal(0.0455, mild.PValue, 4);
        Assert.False(mild.Mismatch); // an ordinary wobble is not flagged
        Assert.Equal(16, bad.ChiSquare, 9);
        Assert.Equal(6.334e-5, bad.PValue, 7);
        Assert.True(bad.Mismatch);
    }

    [Fact]
    public void UnequalAllocationsAreRespected()
    {
        // 70 / 30 configured; 7000 / 3000 observed is exactly on target, 5000 / 5000 is far off.
        Assert.False(SampleRatioMismatch.Test([(7000, 7000), (3000, 3000)])!.Mismatch);
        Assert.True(SampleRatioMismatch.Test([(5000, 7000), (5000, 3000)])!.Mismatch);
    }

    [Fact]
    public void ThreeWaySplitUsesTwoDegreesOfFreedom()
    {
        SampleRatioMismatchResult result = SampleRatioMismatch.Test([(3400, 3333), (3300, 3333), (3300, 3334)])!;

        Assert.Equal(2, result.DegreesOfFreedom);
        Assert.False(result.Mismatch);
    }

    [Fact]
    public void ThePartialAllocationIsComparedAmongExposedUsersOnly()
    {
        // 10% of traffic in the experiment, split 5% / 5%: only the ratio between arms matters.
        Assert.False(SampleRatioMismatch.Test([(5010, 500), (4990, 500)])!.Mismatch);
    }

    [Fact]
    public void UsersInAVariationWithNoTrafficAreAlwaysAMismatch()
    {
        SampleRatioMismatchResult result = SampleRatioMismatch.Test([(100, 5000), (100, 5000), (1, 0)])!;

        Assert.True(result.Mismatch);
        Assert.Equal(0, result.PValue);
    }

    [Fact]
    public void NothingToTestReturnsNull()
    {
        Assert.Null(SampleRatioMismatch.Test([(0, 5000), (0, 5000)]));
        Assert.Null(SampleRatioMismatch.Test([(100, 10_000)]));
        Assert.Null(SampleRatioMismatch.Test([(100, 10_000), (0, 0)]));
    }

    [Fact]
    public void ThresholdIsConfigurable()
    {
        (long, int)[] groups = [(5100, 5000), (4900, 5000)];

        Assert.False(SampleRatioMismatch.Test(groups)!.Mismatch);
        Assert.True(SampleRatioMismatch.Test(groups, threshold: 0.05)!.Mismatch);
    }

    [Fact]
    public void FalseAlarmRateMatchesTheThreshold()
    {
        // Under a true 50/50 split, p < 0.05 should happen about 5% of the time.
        var random = new SeededRandom(11);
        const int trials = 2_000;
        int flagged = 0;
        for (int t = 0; t < trials; t++)
        {
            long a = 0;
            for (int i = 0; i < 500; i++)
            {
                if (random.NextOpenUnit() < 0.5)
                {
                    a++;
                }
            }

            if (SampleRatioMismatch.Test([(a, 5000), (500 - a, 5000)], threshold: 0.05)!.Mismatch)
            {
                flagged++;
            }
        }

        Assert.InRange((double)flagged / trials, 0.035, 0.065);
    }
}

public class ResultsAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static ResultsAggregator New() => new(["control", "treatment"]);

    [Fact]
    public void CountsUniqueUsersPerVariation()
    {
        ResultsAggregator agg = New();
        agg.AddExposure("u1", "control", T0);
        agg.AddExposure("u1", "control", T0.AddMinutes(5)); // repeated exposure is still one user
        agg.AddExposure("u2", "treatment", T0);
        agg.AddExposure("u3", "treatment", T0);

        AggregatedCounts result = agg.Complete();

        Assert.Equal([("control", 1L, 0L), ("treatment", 2L, 0L)], result.Counts);
    }

    [Fact]
    public void ConversionsCountOncePerUserAndOnlyAfterExposure()
    {
        ResultsAggregator agg = New();
        agg.AddExposure("after", "control", T0);
        agg.AddConversion("after", T0.AddMinutes(1));
        agg.AddConversion("after", T0.AddMinutes(2)); // two conversions, one converted user
        agg.AddExposure("before", "control", T0);
        agg.AddConversion("before", T0.AddMinutes(-10)); // converted before ever seeing the variation: not caused by it
        agg.AddExposure("same-instant", "control", T0);
        agg.AddConversion("same-instant", T0);

        AggregatedCounts result = agg.Complete();

        Assert.Equal(("control", 3L, 2L), result.Counts[0]);
    }

    [Fact]
    public void AConversionBeforeAnyExposureOfALaterRepeatStillDoesNotCount()
    {
        ResultsAggregator agg = New();
        agg.AddConversion("u", T0.AddMinutes(-5));
        agg.AddExposure("u", "control", T0);
        agg.AddExposure("u", "control", T0.AddHours(1));

        Assert.Equal(("control", 1L, 0L), agg.Complete().Counts[0]);
    }

    [Fact]
    public void ResultsDoNotDependOnTheOrderEventsArrive()
    {
        ResultsAggregator forwards = New();
        forwards.AddExposure("u", "treatment", T0);
        forwards.AddConversion("u", T0.AddMinutes(1));

        ResultsAggregator backwards = New();
        backwards.AddConversion("u", T0.AddMinutes(1));
        backwards.AddExposure("u", "treatment", T0);

        Assert.Equal(forwards.Complete().Counts, backwards.Complete().Counts);
    }

    [Fact]
    public void UsersSeenInTwoVariationsAreExcludedEverywhereAndCountedSeparately()
    {
        ResultsAggregator agg = New();
        agg.AddExposure("clean", "control", T0);
        agg.AddExposure("both", "control", T0);
        agg.AddExposure("both", "treatment", T0.AddMinutes(1));
        agg.AddConversion("both", T0.AddMinutes(2));

        AggregatedCounts result = agg.Complete();

        Assert.Equal(1, result.ExcludedUsers);
        Assert.Equal(("control", 1L, 0L), result.Counts[0]);
        Assert.Equal(("treatment", 0L, 0L), result.Counts[1]);
    }

    [Fact]
    public void UsersOnlySeenInAnUndefinedVariationAreReported()
    {
        ResultsAggregator agg = New();
        agg.AddExposure("u", "ghost", T0);
        agg.AddExposure("v", "control", T0);

        AggregatedCounts result = agg.Complete();

        Assert.Equal(1, result.UnknownVariationUsers);
        Assert.Equal(("control", 1L, 0L), result.Counts[0]);
    }

    [Fact]
    public void ConversionsFromUsersNeverExposedAreIgnored()
    {
        ResultsAggregator agg = New();
        agg.AddConversion("stranger", T0);

        AggregatedCounts result = agg.Complete();

        Assert.All(result.Counts, c => Assert.Equal((0L, 0L), (c.Exposed, c.Converted)));
        Assert.Equal(0, result.ExcludedUsers);
    }

    [Fact]
    public void VariationsWithNoUsersStillAppearWithZeros()
    {
        Assert.Equal([("control", 0L, 0L), ("treatment", 0L, 0L)], New().Complete().Counts);
    }
}
