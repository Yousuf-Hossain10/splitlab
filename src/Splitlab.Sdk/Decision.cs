namespace Splitlab.Sdk;

/// <summary>Why a decision came out the way it did.</summary>
public enum DecisionReason
{
    /// <summary>The user was bucketed into a variation.</summary>
    Assigned,

    /// <summary>The user's bucket is outside the experiment's allocated traffic.</summary>
    NotInExperiment,

    /// <summary>The loaded configuration has no experiment with that key.</summary>
    ExperimentNotFound,

    /// <summary>No configuration has been loaded yet, so nothing could be decided.</summary>
    ConfigNotLoaded,
}

/// <summary>The result of a decision. <see cref="VariationKey"/> is always usable: it falls back to the caller's default.</summary>
/// <param name="ExperimentKey">The experiment that was asked about.</param>
/// <param name="VariationKey">The assigned variation, or the caller-supplied default when not assigned.</param>
/// <param name="Reason">Why this variation was returned.</param>
public sealed record Decision(string ExperimentKey, string VariationKey, DecisionReason Reason)
{
    /// <summary>Whether the user was actually bucketed into the experiment.</summary>
    public bool IsAssigned => Reason == DecisionReason.Assigned;
}
