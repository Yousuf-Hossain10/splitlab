using Splitlab.Sdk.Bucketing;

namespace Splitlab.Sdk.Tests.Bucketing;

public class BucketerTests
{
    private static Experiment FiftyFifty(string key = "checkout_flow") =>
        new(key, [new Variation("control", 5_000), new Variation("treatment", 5_000)]);

    // Golden values. Every Splitlab SDK, in any language, must reproduce these.
    // Input to the hash is bucketingId + U+001F + experimentKey, seed 1 (ADR-0003).
    [Theory]
    [InlineData("user-1", "checkout_flow", 6376)]
    [InlineData("user-2", "checkout_flow", 2071)]
    [InlineData("user-1", "pricing_page", 9393)]
    [InlineData("ünïcode-用户", "exp", 154)]
    public void BucketValueMatchesGoldenVectors(string bucketingId, string experimentKey, int expected)
    {
        Assert.Equal(expected, Bucketer.BucketValue(bucketingId, experimentKey));
    }

    [Fact]
    public void BucketValueIsAlwaysInsideTheBucketSpace()
    {
        for (int i = 0; i < 20_000; i++)
        {
            int value = Bucketer.BucketValue($"user-{i}", "range_check");
            Assert.InRange(value, 0, Bucketer.BucketSpace - 1);
        }
    }

    [Fact]
    public void SameUserAndExperimentAlwaysGetTheSameVariation()
    {
        Experiment experiment = FiftyFifty();

        string? first = Bucketer.Assign("user-42", experiment);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, Bucketer.Assign("user-42", experiment));
        }
    }

    [Fact]
    public void AssignmentFollowsTheBucketValueAndVariationOrder()
    {
        Experiment experiment = FiftyFifty();

        // user-1 is in bucket 6376 (>= 5000), so it falls in the second range.
        Assert.Equal("treatment", Bucketer.Assign("user-1", experiment));
        // user-2 is in bucket 2071 (< 5000), so it falls in the first range.
        Assert.Equal("control", Bucketer.Assign("user-2", experiment));
    }

    [Fact]
    public void UsersOutsideAllocatedTrafficGetNoVariation()
    {
        var experiment = new Experiment("checkout_flow", [new Variation("only", 6_000)]);

        // user-1 is in bucket 6376, which is past the allocated range [0, 6000).
        Assert.Null(Bucketer.Assign("user-1", experiment));
        // user-2 is in bucket 2071, inside the range.
        Assert.Equal("only", Bucketer.Assign("user-2", experiment));
    }

    [Fact]
    public void ZeroTrafficVariationIsNeverAssigned()
    {
        var experiment = new Experiment("exp", [new Variation("off", 0), new Variation("on", 10_000)]);

        for (int i = 0; i < 5_000; i++)
        {
            Assert.Equal("on", Bucketer.Assign($"user-{i}", experiment));
        }
    }

    [Fact]
    public void FullTrafficAlwaysProducesAVariation()
    {
        Experiment experiment = FiftyFifty();

        for (int i = 0; i < 5_000; i++)
        {
            Assert.NotNull(Bucketer.Assign($"user-{i}", experiment));
        }
    }

    [Fact]
    public void FiftyFiftySplitStaysWithinOnePercentAtOneHundredThousandUsers()
    {
        Experiment experiment = FiftyFifty();
        const int users = 100_000;

        int treatment = 0;
        for (int i = 0; i < users; i++)
        {
            if (Bucketer.Assign($"user-{i}", experiment) == "treatment")
            {
                treatment++;
            }
        }

        double share = (double)treatment / users;
        Assert.InRange(share, 0.49, 0.51);
    }

    [Fact]
    public void UnevenSplitMatchesConfiguredWeights()
    {
        var experiment = new Experiment(
            "weights",
            [new Variation("a", 7_000), new Variation("b", 2_000), new Variation("c", 1_000)]);
        const int users = 100_000;

        var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0 };
        for (int i = 0; i < users; i++)
        {
            counts[Bucketer.Assign($"user-{i}", experiment)!]++;
        }

        Assert.InRange((double)counts["a"] / users, 0.69, 0.71);
        Assert.InRange((double)counts["b"] / users, 0.19, 0.21);
        Assert.InRange((double)counts["c"] / users, 0.09, 0.11);
    }

    [Fact]
    public void PartialAllocationAssignsRoughlyTheAllocatedShare()
    {
        var experiment = new Experiment("holdback", [new Variation("a", 1_000), new Variation("b", 1_000)]);
        const int users = 100_000;

        int assigned = 0;
        for (int i = 0; i < users; i++)
        {
            if (Bucketer.Assign($"user-{i}", experiment) is not null)
            {
                assigned++;
            }
        }

        Assert.InRange((double)assigned / users, 0.19, 0.21);
    }

    [Fact]
    public void DifferentExperimentsBucketTheSameUserIndependently()
    {
        // If two experiments used the same buckets, users in "treatment" of one would always be in
        // "treatment" of the other. Check that the overlap looks like independence (about 25%).
        Experiment first = FiftyFifty("experiment_one");
        Experiment second = FiftyFifty("experiment_two");
        const int users = 100_000;

        int bothTreatment = 0;
        for (int i = 0; i < users; i++)
        {
            string id = $"user-{i}";
            if (Bucketer.Assign(id, first) == "treatment" && Bucketer.Assign(id, second) == "treatment")
            {
                bothTreatment++;
            }
        }

        Assert.InRange((double)bothTreatment / users, 0.24, 0.26);
    }

    [Fact]
    public void ConcatenationBoundaryDoesNotCauseCollisions()
    {
        Assert.False(Bucketer.BucketValue("ab", "c") == Bucketer.BucketValue("a", "bc"));
    }

    [Fact]
    public void BlankBucketingIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Bucketer.Assign("", FiftyFifty()));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Bucketer.Assign(null!, FiftyFifty()));
        Assert.Throws<ArgumentNullException>(() => Bucketer.Assign("user-1", null!));
        Assert.Throws<ArgumentNullException>(() => Bucketer.BucketValue("user-1", null!));
    }
}
