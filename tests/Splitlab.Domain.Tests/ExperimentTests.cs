using Splitlab.Domain;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Domain.Tests;

public class ExperimentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static Experiment NewExperiment(params VariationSpec[] variations) =>
        Experiment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "checkout_flow",
            "Checkout flow",
            variations.Length == 0 ? [new("control", 5_000), new("treatment", 5_000)] : variations,
            Now);

    [Fact]
    public void NewExperimentIsADraftAtVersionOne()
    {
        Experiment e = NewExperiment();

        Assert.Equal(ExperimentStatus.Draft, e.Status);
        Assert.Equal(1, e.Version);
        Assert.Equal(["control", "treatment"], e.Variations.Select(v => v.Key));
    }

    [Fact]
    public void LifecycleDraftToRunningToStoppedBumpsVersionEachStep()
    {
        Experiment e = NewExperiment();

        e.Start(Now);
        Assert.Equal((ExperimentStatus.Running, 2), (e.Status, e.Version));

        e.Stop(Now);
        Assert.Equal((ExperimentStatus.Stopped, 3), (e.Status, e.Version));
    }

    [Fact]
    public void CannotStartTwiceOrStopADraft()
    {
        Experiment e = NewExperiment();

        Assert.Throws<DomainException>(() => e.Stop(Now));
        e.Start(Now);
        Assert.Throws<DomainException>(() => e.Start(Now));
    }

    [Fact]
    public void StartingNeedsAtLeastTwoVariations()
    {
        Experiment e = NewExperiment(new VariationSpec("only", 10_000));

        Assert.Throws<DomainException>(() => e.Start(Now));
    }

    [Fact]
    public void VariationsCannotChangeOnceStarted()
    {
        Experiment e = NewExperiment();
        e.Start(Now);

        Assert.Throws<DomainException>(() => e.ReplaceVariations([new("a", 1), new("b", 1)], Now));
    }

    [Fact]
    public void ReplacingVariationsInADraftKeepsOrderAndBumpsVersion()
    {
        Experiment e = NewExperiment();

        e.ReplaceVariations([new("b", 1_000), new("a", 2_000), new("c", 0)], Now);

        Assert.Equal(["b", "a", "c"], e.Variations.Select(v => v.Key));
        Assert.Equal(2, e.Version);
    }

    [Fact]
    public void InvalidReplacementLeavesTheExperimentUntouched()
    {
        Experiment e = NewExperiment();

        Assert.Throws<DomainException>(() => e.ReplaceVariations([new("a", 9_000), new("b", 9_000)], Now));

        Assert.Equal(["control", "treatment"], e.Variations.Select(v => v.Key));
        Assert.Equal(1, e.Version);
    }

    [Fact]
    public void RejectsDuplicateKeysOverAllocationAndOutOfRangeTraffic()
    {
        Assert.Throws<DomainException>(() => NewExperiment(new("a", 100), new("a", 100)));
        Assert.Throws<DomainException>(() => NewExperiment(new("a", 6_000), new("b", 6_000)));
        Assert.Throws<DomainException>(() => NewExperiment(new VariationSpec("a", -1)));
        Assert.Throws<DomainException>(() => NewExperiment(new VariationSpec("a", 10_001)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    public void RejectsBadKeys(string key)
    {
        Assert.Throws<DomainException>(() => Project.Create(key, "Name", Now));
        Assert.Throws<DomainException>(() => NewExperiment(new VariationSpec(key, 100)));
    }

    [Fact]
    public void ConfigVersionBumpsOnDemand()
    {
        var env = ProjectEnvironment.Create(Guid.NewGuid(), "production", "Production", Now);

        env.BumpConfigVersion();

        Assert.Equal(1, env.ConfigVersion);
    }
}
