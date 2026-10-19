namespace Splitlab.Sdk.Bucketing;

/// <summary>
/// An experiment definition as seen by the SDK: a key and an ordered list of variations.
/// Variation order is part of the contract, because ranges are laid out in that order (ADR-0003).
/// </summary>
public sealed class Experiment
{
    /// <param name="key">Experiment identifier; also salts the hash so experiments bucket independently.</param>
    /// <param name="variations">Ordered variations. Keys must be unique and total allocation at most <see cref="Bucketer.BucketSpace"/>.</param>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    public Experiment(string key, IReadOnlyList<Variation> variations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(variations);

        if (variations.Count == 0)
        {
            throw new ArgumentException("An experiment needs at least one variation.", nameof(variations));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int allocated = 0;
        foreach (Variation variation in variations)
        {
            if (!seen.Add(variation.Key))
            {
                throw new ArgumentException($"Duplicate variation key '{variation.Key}'.", nameof(variations));
            }

            allocated += variation.TrafficBasisPoints;
        }

        if (allocated > Bucketer.BucketSpace)
        {
            throw new ArgumentException(
                $"Variations allocate {allocated} basis points, which is more than the {Bucketer.BucketSpace} available.",
                nameof(variations));
        }

        Key = key;
        Variations = variations.ToArray();
        AllocatedBasisPoints = allocated;
    }

    /// <summary>The experiment key.</summary>
    public string Key { get; }

    /// <summary>The variations in range-layout order.</summary>
    public IReadOnlyList<Variation> Variations { get; }

    /// <summary>Total traffic allocated across all variations, in basis points.</summary>
    public int AllocatedBasisPoints { get; }
}
