using Splitlab.Sdk.Bucketing;

namespace Splitlab.Sdk.Config;

/// <summary>
/// An immutable view of the experiment configuration at one datafile version (ADR-0007).
/// Because it never changes after construction it can be shared across threads without locks.
/// </summary>
public sealed class ConfigSnapshot
{
    private readonly Dictionary<string, Experiment> _experiments;

    internal ConfigSnapshot(string version, string? etag, IEnumerable<Experiment> experiments)
    {
        Version = version;
        ETag = etag;
        _experiments = experiments.ToDictionary(e => e.Key, StringComparer.Ordinal);
    }

    /// <summary>The datafile version this snapshot was built from.</summary>
    public string Version { get; }

    /// <summary>The ETag the datafile was served with, if it came from the network.</summary>
    public string? ETag { get; }

    /// <summary>Number of experiments in the snapshot.</summary>
    public int ExperimentCount => _experiments.Count;

    /// <summary>Looks up an experiment by key.</summary>
    public bool TryGetExperiment(string key, out Experiment experiment)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _experiments.TryGetValue(key, out experiment!);
    }
}
