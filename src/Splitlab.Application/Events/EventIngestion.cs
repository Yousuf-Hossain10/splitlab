using System.Diagnostics.Metrics;
using Splitlab.Domain;
using Splitlab.Domain.Events;

namespace Splitlab.Application.Events;

/// <summary>
/// Where accepted events go. The default implementation is an in-process bounded queue; a message broker can
/// replace it without touching the endpoint (ADR-0005).
/// </summary>
public interface IEventSink
{
    /// <summary>Queues the whole batch or none of it. Returns false when there is not room (apply backpressure).</summary>
    bool TryAccept(IReadOnlyList<EventRecord> events);
}

/// <summary>Persists a batch, ignoring events already stored. Returns how many were new.</summary>
public interface IEventStore
{
    Task<int> InsertAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct);
}

/// <summary>Pipeline metrics named as in ADR-0009. Exporting them is configured by the host.</summary>
public static class PipelineMetrics
{
    public const string MeterName = "Splitlab";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Events = Meter.CreateCounter<long>("splitlab_ingest_events_total");

    public static Histogram<double> FlushDuration { get; } =
        Meter.CreateHistogram<double>("splitlab_ingest_flush_duration_seconds", unit: "s");

    /// <param name="outcome">accepted, rejected, duplicate, dropped or throttled.</param>
    public static void Count(string outcome, long count)
    {
        if (count > 0)
        {
            Events.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public static void RegisterQueueDepth(Func<int> depth) =>
        Meter.CreateObservableGauge("splitlab_ingest_queue_depth", depth);
}

/// <summary>One event as sent by an SDK, before validation.</summary>
public sealed record RawEvent(
    string? EventId,
    string? Type,
    string? ExperimentKey,
    string? VariationKey,
    string? Name,
    double? Value,
    string? UserId,
    DateTimeOffset? Timestamp);

public sealed record Rejection(int Index, string Reason);

/// <param name="QueueFull">The batch was valid but there is no room; nothing was queued and the SDK should retry later.</param>
public sealed record IngestResult(int Accepted, IReadOnlyList<Rejection> Rejected, bool QueueFull);

public sealed class EventIngestionService(IEventSink sink, TimeProvider clock)
{
    public const int MaxBatch = 500;

    /// <summary>
    /// Validates a batch and queues the valid events. Invalid events are reported individually and do not block the rest.
    /// </summary>
    /// <exception cref="DomainException">The batch is missing, empty or larger than <see cref="MaxBatch"/>.</exception>
    public IngestResult Ingest(Guid projectId, Guid environmentId, IReadOnlyList<RawEvent>? events)
    {
        if (events is null || events.Count == 0)
        {
            throw new DomainException("events must contain at least one event.");
        }

        if (events.Count > MaxBatch)
        {
            throw new DomainException($"A batch can hold at most {MaxBatch} events; got {events.Count}.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        var valid = new List<EventRecord>(events.Count);
        var rejected = new List<Rejection>();

        for (int i = 0; i < events.Count; i++)
        {
            RawEvent e = events[i];
            if (EventRecord.TryCreate(
                    projectId, environmentId, e?.EventId, e?.Type, e?.ExperimentKey, e?.VariationKey,
                    e?.Name, e?.Value, e?.UserId, e?.Timestamp, now, out EventRecord? record, out string? error))
            {
                valid.Add(record!);
            }
            else
            {
                rejected.Add(new Rejection(i, error!));
            }
        }

        PipelineMetrics.Count("rejected", rejected.Count);

        if (valid.Count > 0 && !sink.TryAccept(valid))
        {
            PipelineMetrics.Count("throttled", valid.Count);
            return new IngestResult(0, rejected, QueueFull: true);
        }

        PipelineMetrics.Count("accepted", valid.Count);
        return new IngestResult(valid.Count, rejected, QueueFull: false);
    }
}
