namespace Splitlab.Sdk;

/// <summary>Settings for <see cref="SplitlabClient"/>.</summary>
public sealed class SplitlabClientOptions
{
    /// <summary>Time between datafile polls. Default 30 seconds (ADR-0007).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum random fraction added to each poll delay, from 0 to 1, so fleets do not poll in lockstep. Default 0.2.</summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>Upper bound on the retry delay after consecutive failures. Default 5 minutes.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional datafile JSON used to serve decisions before the first successful fetch.</summary>
    public string? EmbeddedDatafile { get; init; }
}
