namespace Splitlab.Sdk.Events;

/// <summary>
/// One exposure or conversion, exactly as it will be sent. It is immutable and carries its own id and timestamp, so a
/// retry resends identical data and the server can recognise the repeat and store it once (ADR-0005).
/// </summary>
/// <param name="EventId">Client-generated id, unique per event.</param>
/// <param name="Type">Either <see cref="Exposure"/> or <see cref="Conversion"/>.</param>
/// <param name="ExperimentKey">For an exposure, the experiment the user was shown.</param>
/// <param name="VariationKey">For an exposure, the variation the user saw.</param>
/// <param name="Name">For a conversion, the metric name, such as <c>purchase</c>.</param>
/// <param name="Value">For a conversion, an optional number such as revenue.</param>
/// <param name="UserId">The same user id that was used for bucketing.</param>
/// <param name="Timestamp">When it happened (UTC, millisecond precision).</param>
public sealed record TrackedEvent(
    string EventId,
    string Type,
    string? ExperimentKey,
    string? VariationKey,
    string? Name,
    double? Value,
    string UserId,
    DateTimeOffset Timestamp)
{
    /// <summary>The <see cref="Type"/> of an event recording that a user was shown a variation.</summary>
    public const string Exposure = "exposure";

    /// <summary>The <see cref="Type"/> of an event recording that a user did something being measured.</summary>
    public const string Conversion = "conversion";
}
