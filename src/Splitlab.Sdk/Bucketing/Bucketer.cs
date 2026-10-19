using System.Text;

namespace Splitlab.Sdk.Bucketing;

/// <summary>
/// Deterministic, stateless assignment of a user to an experiment variation (see ADR-0003).
/// The same inputs give the same result on every machine and in every SDK that follows the spec.
/// </summary>
public static class Bucketer
{
    /// <summary>Size of the bucket space: 10,000 buckets, so one bucket is 0.01% of traffic.</summary>
    public const int BucketSpace = 10_000;

    private const uint HashSeed = 1;

    // Separates the two hash inputs so ("ab", "c") and ("a", "bc") cannot collide.
    private const char Separator = '\u001F';

    /// <summary>Returns the bucket, from 0 to <see cref="BucketSpace"/> - 1, for a user in an experiment.</summary>
    public static int BucketValue(string bucketingId, string experimentKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketingId);
        ArgumentException.ThrowIfNullOrEmpty(experimentKey);

        string input = new StringBuilder(bucketingId.Length + experimentKey.Length + 1)
            .Append(bucketingId)
            .Append(Separator)
            .Append(experimentKey)
            .ToString();

        uint hash = MurmurHash3.Hash32(input, HashSeed);

        // floor(hash / 2^32 * BucketSpace) using integer arithmetic, which is exact.
        return (int)(((ulong)hash * BucketSpace) >> 32);
    }

    /// <summary>
    /// Returns the key of the variation the user is assigned to, or <c>null</c> when the user's
    /// bucket falls outside the experiment's allocated traffic.
    /// </summary>
    public static string? Assign(string bucketingId, Experiment experiment)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketingId);
        ArgumentNullException.ThrowIfNull(experiment);

        int bucket = BucketValue(bucketingId, experiment.Key);

        int rangeEnd = 0;
        foreach (Variation variation in experiment.Variations)
        {
            rangeEnd += variation.TrafficBasisPoints;
            if (bucket < rangeEnd)
            {
                return variation.Key;
            }
        }

        return null;
    }
}
