using Splitlab.Domain.Events;
using Splitlab.Domain.Projects;

namespace Splitlab.Domain.Statistics;

/// <summary>
/// What we remember about one user in one experiment: the single variation they were exposed to and when they first
/// saw it. This is the rollup behind results (ADR-0004): small, updated as events arrive, and enough to answer
/// "how many unique users saw each variation" without scanning raw events.
/// </summary>
public sealed class ExperimentUserState : ITenantOwned
{
    public Guid ProjectId { get; init; }

    public Guid EnvironmentId { get; init; }

    public string ExperimentKey { get; init; } = "";

    public string UserId { get; init; } = "";

    /// <summary>The variation first seen. Meaningless once <see cref="Conflicted"/>.</summary>
    public string VariationKey { get; init; } = "";

    /// <summary>Earliest exposure, as Unix milliseconds (UTC). Plain numbers compare identically on every database.</summary>
    public long FirstExposureMs { get; init; }

    /// <summary>The user has been exposed to more than one variation, so they are excluded from results (ADR-0006).</summary>
    public bool Conflicted { get; init; }
}

/// <summary>The latest time a user performed a conversion metric.</summary>
public sealed class MetricUserState : ITenantOwned
{
    public Guid ProjectId { get; init; }

    public Guid EnvironmentId { get; init; }

    public string Metric { get; init; } = "";

    public string UserId { get; init; } = "";

    public long LastConversionMs { get; init; }
}

public sealed record ExposureDelta(
    Guid ProjectId, Guid EnvironmentId, string ExperimentKey, string UserId, string VariationKey, long FirstExposureMs, bool Conflicted);

public sealed record ConversionDelta(Guid ProjectId, Guid EnvironmentId, string Metric, string UserId, long LastConversionMs);

/// <summary>
/// The rollup changes caused by a batch of events, already merged per user. The same merge rules are applied again
/// by the database when these are folded into the stored state, and they are order-independent and repeatable:
/// <list type="bullet">
/// <item>first exposure time is the minimum seen;</item>
/// <item>a user is conflicted if more than one distinct variation was ever seen;</item>
/// <item>last conversion time is the maximum seen.</item>
/// </list>
/// So events can arrive in any order, in any batches, and a retried batch changes nothing.
/// </summary>
public sealed class RollupBatch
{
    private RollupBatch(IReadOnlyList<ExposureDelta> exposures, IReadOnlyList<ConversionDelta> conversions)
    {
        Exposures = exposures;
        Conversions = conversions;
    }

    public IReadOnlyList<ExposureDelta> Exposures { get; }

    public IReadOnlyList<ConversionDelta> Conversions { get; }

    public static RollupBatch From(IEnumerable<EventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var exposures = new Dictionary<(Guid, string, string), ExposureDelta>();
        var conversions = new Dictionary<(Guid, string, string), ConversionDelta>();

        foreach (EventRecord e in events)
        {
            long ms = e.OccurredAt.ToUnixTimeMilliseconds();

            if (e.Kind == EventKind.Exposure)
            {
                var key = (e.EnvironmentId, e.ExperimentKey!, e.UserId);
                exposures[key] = exposures.TryGetValue(key, out ExposureDelta? existing)
                    ? existing with
                    {
                        FirstExposureMs = Math.Min(existing.FirstExposureMs, ms),
                        Conflicted = existing.Conflicted || !string.Equals(existing.VariationKey, e.VariationKey, StringComparison.Ordinal),
                    }
                    : new ExposureDelta(e.ProjectId, e.EnvironmentId, e.ExperimentKey!, e.UserId, e.VariationKey!, ms, false);
            }
            else
            {
                var key = (e.EnvironmentId, e.Name!, e.UserId);
                conversions[key] = conversions.TryGetValue(key, out ConversionDelta? existing)
                    ? existing with { LastConversionMs = Math.Max(existing.LastConversionMs, ms) }
                    : new ConversionDelta(e.ProjectId, e.EnvironmentId, e.Name!, e.UserId, ms);
            }
        }

        return new RollupBatch(exposures.Values.ToList(), conversions.Values.ToList());
    }
}
