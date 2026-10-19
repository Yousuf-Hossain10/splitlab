namespace Splitlab.Sdk.Bucketing;

/// <summary>One arm of an experiment and the share of traffic it receives.</summary>
public sealed class Variation
{
    /// <param name="key">Identifier unique within its experiment.</param>
    /// <param name="trafficBasisPoints">
    /// Share of all traffic, in basis points (1 = 0.01%), from 0 to <see cref="Bucketer.BucketSpace"/>.
    /// </param>
    public Variation(string key, int trafficBasisPoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(trafficBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trafficBasisPoints, Bucketer.BucketSpace);

        Key = key;
        TrafficBasisPoints = trafficBasisPoints;
    }

    /// <summary>The variation key, unique within its experiment.</summary>
    public string Key { get; }

    /// <summary>Share of all traffic in basis points (1 = 0.01%).</summary>
    public int TrafficBasisPoints { get; }
}
