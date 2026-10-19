using Splitlab.Domain.Projects;

namespace Splitlab.Domain.Experiments;

public enum ExperimentStatus
{
    Draft,
    Running,
    Stopped,
}

/// <summary>One arm of an experiment: a key and its share of all traffic in basis points (1 = 0.01%).</summary>
public readonly record struct VariationSpec(string Key, int TrafficBasisPoints);

public sealed class Variation
{
    private Variation()
    {
    }

    // Surrogate key for the owned table.
    public int Id { get; private set; }

    public int Position { get; private set; }

    public string Key { get; private set; } = "";

    public int TrafficBasisPoints { get; private set; }

    internal static Variation Create(int position, VariationSpec spec) => new()
    {
        Position = position,
        Key = spec.Key,
        TrafficBasisPoints = spec.TrafficBasisPoints,
    };
}

/// <summary>
/// An A/B experiment in one environment. Variations can only change while it is a draft,
/// so a published datafile never changes under running traffic.
/// </summary>
public sealed class Experiment : ITenantOwned
{
    /// <summary>Size of the bucket space; must match the SDK's <c>Bucketer.BucketSpace</c> (ADR-0003).</summary>
    public const int BucketSpace = 10_000;

    private readonly List<Variation> _variations = [];

    private Experiment()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid EnvironmentId { get; private set; }

    public string Key { get; private set; } = "";

    public string Name { get; private set; } = "";

    public ExperimentStatus Status { get; private set; }

    /// <summary>Optimistic concurrency token, incremented on every change.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Variations in range-layout order.</summary>
    public IReadOnlyList<Variation> Variations => _variations.OrderBy(v => v.Position).ToList();

    public static Experiment Create(
        Guid projectId,
        Guid environmentId,
        string? key,
        string? name,
        IEnumerable<VariationSpec> variations,
        DateTimeOffset now)
    {
        var experiment = new Experiment
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Key = Keys.Validate(key, "Experiment key"),
            Name = Keys.RequireName(name, "Experiment name"),
            Status = ExperimentStatus.Draft,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        experiment.SetVariations(variations);
        return experiment;
    }

    public void ReplaceVariations(IEnumerable<VariationSpec> variations, DateTimeOffset now)
    {
        if (Status != ExperimentStatus.Draft)
        {
            throw new DomainException("Variations can only be changed while the experiment is a draft.");
        }

        SetVariations(variations);
        Touch(now);
    }

    /// <summary>Publishes the experiment. Needs at least two variations so there is something to compare.</summary>
    public void Start(DateTimeOffset now)
    {
        if (Status != ExperimentStatus.Draft)
        {
            throw new DomainException($"Only a draft experiment can be started; this one is {Status}.");
        }

        if (_variations.Count < 2)
        {
            throw new DomainException("An experiment needs at least two variations to start.");
        }

        Status = ExperimentStatus.Running;
        Touch(now);
    }

    public void Stop(DateTimeOffset now)
    {
        if (Status != ExperimentStatus.Running)
        {
            throw new DomainException($"Only a running experiment can be stopped; this one is {Status}.");
        }

        Status = ExperimentStatus.Stopped;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private void SetVariations(IEnumerable<VariationSpec> variations)
    {
        ArgumentNullException.ThrowIfNull(variations);

        var specs = variations.ToList();
        if (specs.Count == 0)
        {
            throw new DomainException("An experiment needs at least one variation.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int allocated = 0;
        foreach (VariationSpec spec in specs)
        {
            Keys.Validate(spec.Key, "Variation key");
            if (!seen.Add(spec.Key))
            {
                throw new DomainException($"Duplicate variation key '{spec.Key}'.");
            }

            if (spec.TrafficBasisPoints is < 0 or > BucketSpace)
            {
                throw new DomainException($"Variation '{spec.Key}' traffic must be between 0 and {BucketSpace} basis points.");
            }

            allocated += spec.TrafficBasisPoints;
        }

        if (allocated > BucketSpace)
        {
            throw new DomainException($"Variations allocate {allocated} basis points, more than the {BucketSpace} available.");
        }

        _variations.Clear();
        for (int i = 0; i < specs.Count; i++)
        {
            _variations.Add(Variation.Create(i, specs[i]));
        }
    }
}
