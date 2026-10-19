using Splitlab.Domain.Events;
using Splitlab.Domain.Statistics;

namespace Splitlab.Domain.Tests.Statistics;

public class RollupBatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Environment = Guid.NewGuid();

    private static EventRecord Exposure(string user, string variation, int minutes, string experiment = "checkout")
    {
        Assert.True(EventRecord.TryCreate(
            Project, Environment, Guid.NewGuid().ToString("N"), "exposure", experiment, variation, null, null, user, Now.AddMinutes(-minutes), Now, out EventRecord? e, out _));
        return e!;
    }

    private static EventRecord Conversion(string user, int minutes, string metric = "purchase")
    {
        Assert.True(EventRecord.TryCreate(
            Project, Environment, Guid.NewGuid().ToString("N"), "conversion", null, null, metric, null, user, Now.AddMinutes(-minutes), Now, out EventRecord? e, out _));
        return e!;
    }

    [Fact]
    public void RepeatedExposuresOfOneUserCollapseToTheEarliest()
    {
        RollupBatch batch = RollupBatch.From([Exposure("u", "control", 5), Exposure("u", "control", 9), Exposure("u", "control", 1)]);

        ExposureDelta delta = Assert.Single(batch.Exposures);
        Assert.Equal(Now.AddMinutes(-9).ToUnixTimeMilliseconds(), delta.FirstExposureMs);
        Assert.False(delta.Conflicted);
        Assert.Equal("control", delta.VariationKey);
    }

    [Fact]
    public void TwoVariationsForOneUserMarkTheUserConflicted()
    {
        RollupBatch batch = RollupBatch.From([Exposure("u", "control", 1), Exposure("u", "treatment", 2), Exposure("u", "control", 3)]);

        Assert.True(Assert.Single(batch.Exposures).Conflicted);
    }

    [Fact]
    public void UsersExperimentsAndMetricsAreKeptApart()
    {
        RollupBatch batch = RollupBatch.From(
        [
            Exposure("a", "control", 1),
            Exposure("b", "control", 1),
            Exposure("a", "control", 1, experiment: "other"),
            Conversion("a", 2),
            Conversion("a", 2, metric: "signup"),
        ]);

        Assert.Equal(3, batch.Exposures.Count);
        Assert.Equal(2, batch.Conversions.Count);
    }

    [Fact]
    public void ConversionsKeepTheLatestTime()
    {
        RollupBatch batch = RollupBatch.From([Conversion("u", 4), Conversion("u", 9), Conversion("u", 1)]); // 9, 4 and 1 minutes ago

        Assert.Equal(Now.AddMinutes(-1).ToUnixTimeMilliseconds(), Assert.Single(batch.Conversions).LastConversionMs);
    }

    [Fact]
    public void TheResultDoesNotDependOnEventOrder()
    {
        EventRecord[] events =
        [
            Exposure("u", "control", 3), Exposure("u", "treatment", 1), Exposure("v", "control", 2),
            Conversion("u", 5), Conversion("v", 1), Conversion("v", 8),
        ];

        RollupBatch forwards = RollupBatch.From(events);
        RollupBatch backwards = RollupBatch.From(events.Reverse());

        // For a conflicted user the stored variation is deliberately unspecified (results ignore it), so compare without it.
        static IEnumerable<ExposureDelta> Canonical(RollupBatch b) =>
            b.Exposures.Select(e => e.Conflicted ? e with { VariationKey = "" } : e).OrderBy(e => e.UserId);

        Assert.Equal(Canonical(forwards), Canonical(backwards));
        Assert.Contains(forwards.Exposures, e => e is { UserId: "u", Conflicted: true });
        Assert.Equal(forwards.Conversions.OrderBy(e => e.UserId), backwards.Conversions.OrderBy(e => e.UserId));
    }

    [Fact]
    public void AnEmptyBatchHasNoDeltas()
    {
        RollupBatch batch = RollupBatch.From([]);

        Assert.Empty(batch.Exposures);
        Assert.Empty(batch.Conversions);
    }
}
