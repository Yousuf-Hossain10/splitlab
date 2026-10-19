using Splitlab.Sdk.Bucketing;

namespace Splitlab.Sdk.Tests.Bucketing;

public class ExperimentTests
{
    [Fact]
    public void ValidExperimentExposesItsDefinition()
    {
        var experiment = new Experiment(
            "checkout_flow",
            [new Variation("control", 5_000), new Variation("treatment", 5_000)]);

        Assert.Equal("checkout_flow", experiment.Key);
        Assert.Equal(2, experiment.Variations.Count);
        Assert.Equal(10_000, experiment.AllocatedBasisPoints);
    }

    [Fact]
    public void AllocatingLessThanFullTrafficIsAllowed()
    {
        var experiment = new Experiment(
            "holdback",
            [new Variation("control", 1_000), new Variation("treatment", 1_000)]);

        Assert.Equal(2_000, experiment.AllocatedBasisPoints);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankExperimentKeyIsRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => new Experiment(key, [new Variation("a", 100)]));
    }

    [Fact]
    public void NullExperimentKeyIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new Experiment(null!, [new Variation("a", 100)]));
    }

    [Fact]
    public void ExperimentWithoutVariationsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Experiment("exp", []));
    }

    [Fact]
    public void DuplicateVariationKeysAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new Experiment("exp", [new Variation("a", 100), new Variation("a", 200)]));
    }

    [Fact]
    public void AllocationAboveFullTrafficIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new Experiment("exp", [new Variation("a", 6_000), new Variation("b", 4_001)]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void VariationTrafficOutsideBucketSpaceIsRejected(int basisPoints)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Variation("a", basisPoints));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankVariationKeyIsRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => new Variation(key, 100));
    }
}
