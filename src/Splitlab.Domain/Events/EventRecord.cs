using System.Globalization;
using Splitlab.Domain.Projects;

namespace Splitlab.Domain.Events;

public enum EventKind
{
    /// <summary>A user was shown a variation of an experiment.</summary>
    Exposure,

    /// <summary>A user did something we measure, such as completing a purchase.</summary>
    Conversion,
}

/// <summary>
/// One accepted SDK event. Events are immutable facts; the project and environment come from the SDK key that
/// sent them, never from the payload, so a key cannot write into anyone else's data (ADR-0010).
/// Duplicates are recognised by (environment, event id, timestamp), so an SDK retry must resend the same event unchanged (ADR-0005).
/// </summary>
public sealed class EventRecord : ITenantOwned
{
    public const int MaxEventIdLength = 100;
    public const int MaxUserIdLength = 200;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private EventRecord()
    {
    }

    public Guid ProjectId { get; private set; }

    public Guid EnvironmentId { get; private set; }

    public string EventId { get; private set; } = "";

    public DateTimeOffset OccurredAt { get; private set; }

    public EventKind Kind { get; private set; }

    public string UserId { get; private set; } = "";

    public string? ExperimentKey { get; private set; }

    public string? VariationKey { get; private set; }

    /// <summary>The metric name for a conversion.</summary>
    public string? Name { get; private set; }

    /// <summary>Optional numeric value of a conversion, such as revenue.</summary>
    public double? Value { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Validates one event. On failure <paramref name="error"/> says why and <paramref name="record"/> is null.</summary>
    public static bool TryCreate(
        Guid projectId,
        Guid environmentId,
        string? eventId,
        string? type,
        string? experimentKey,
        string? variationKey,
        string? name,
        double? value,
        string? userId,
        DateTimeOffset? timestamp,
        DateTimeOffset now,
        out EventRecord? record,
        out string? error)
    {
        record = null;
        error = Validate(eventId, type, experimentKey, variationKey, name, value, userId, timestamp, now, out EventKind kind);
        if (error is not null)
        {
            return false;
        }

        // Millisecond precision keeps the dedupe key stable however the client formats the timestamp.
        long ticks = timestamp!.Value.UtcTicks;
        var occurred = new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

        record = new EventRecord
        {
            ProjectId = projectId,
            EnvironmentId = environmentId,
            EventId = eventId!,
            OccurredAt = occurred,
            Kind = kind,
            UserId = userId!,
            ExperimentKey = kind == EventKind.Exposure ? experimentKey : null,
            VariationKey = kind == EventKind.Exposure ? variationKey : null,
            Name = kind == EventKind.Conversion ? name : null,
            Value = kind == EventKind.Conversion ? value : null,
            ReceivedAt = now,
        };
        return true;
    }

    private static string? Validate(
        string? eventId,
        string? type,
        string? experimentKey,
        string? variationKey,
        string? name,
        double? value,
        string? userId,
        DateTimeOffset? timestamp,
        DateTimeOffset now,
        out EventKind kind)
    {
        kind = default;

        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > MaxEventIdLength)
        {
            return $"eventId is required and must be at most {MaxEventIdLength} characters.";
        }

        if (string.IsNullOrEmpty(userId) || userId.Length > MaxUserIdLength)
        {
            return $"userId is required and must be at most {MaxUserIdLength} characters.";
        }

        if (timestamp is null)
        {
            return "timestamp is required, so that retries of the same event can be recognised.";
        }

        if (timestamp < now - MaxAge)
        {
            return $"timestamp is more than {MaxAge.TotalDays.ToString(CultureInfo.InvariantCulture)} days old.";
        }

        if (timestamp > now + MaxClockSkew)
        {
            return "timestamp is in the future.";
        }

        switch (type)
        {
            case "exposure":
                kind = EventKind.Exposure;
                return IsKey(experimentKey) && IsKey(variationKey)
                    ? null
                    : "an exposure needs a valid experimentKey and variationKey.";
            case "conversion":
                kind = EventKind.Conversion;
                if (!IsKey(name))
                {
                    return "a conversion needs a valid name.";
                }

                return value is { } v && !double.IsFinite(v) ? "value must be a finite number." : null;
            default:
                return "type must be 'exposure' or 'conversion'.";
        }
    }

    private static bool IsKey(string? value)
    {
        try
        {
            Keys.Validate(value, "key");
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }
}
