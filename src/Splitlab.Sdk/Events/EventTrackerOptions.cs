namespace Splitlab.Sdk.Events;

/// <summary>Why the tracker is telling the application something.</summary>
public enum EventTrackerProblemKind
{
    /// <summary>A batch was given up on: retries ran out, or the server refused it for good (for example a revoked key).</summary>
    BatchDropped,

    /// <summary>The server accepted a batch but refused some events in it as invalid. They will not be retried.</summary>
    EventsRejected,
}

/// <summary>A problem worth logging. Event delivery never throws into the application, so this is how it finds out.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Message">A short explanation.</param>
/// <param name="EventCount">How many events were affected.</param>
public sealed record EventTrackerProblem(EventTrackerProblemKind Kind, string Message, int EventCount);

/// <summary>Settings for <see cref="EventTracker"/>.</summary>
public sealed class EventTrackerOptions
{
    /// <summary>Most events per request. The server accepts at most 500. Default 500.</summary>
    public int MaxBatchSize { get; init; } = 500;

    /// <summary>How long to wait for a batch to fill before sending what there is. Default 2 seconds.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Most events held in memory while waiting to be sent. Beyond this, new events are dropped (and counted) rather than
    /// letting a slow or unreachable server grow the application's memory without limit. Default 10,000.
    /// </summary>
    public int QueueCapacity { get; init; } = 10_000;

    /// <summary>Tries per batch before it is given up. Default 6.</summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>Delay before the first retry; it doubles each time. Default 1 second.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on the delay between retries, including a delay the server asked for. Default 1 minute.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Random extra delay, as a fraction of the delay, so many clients do not retry in step. Default 0.2.</summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>How long disposing waits to send what is still queued before giving up on it. Default 5 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Called when a batch is dropped or events are rejected. Exceptions it throws are ignored.</summary>
    public Action<EventTrackerProblem>? OnProblem { get; init; }
}

/// <summary>What the tracker has done so far.</summary>
/// <param name="Tracked">Events the application handed over.</param>
/// <param name="Sent">Events the server accepted.</param>
/// <param name="RejectedByServer">Events the server refused as invalid.</param>
/// <param name="Dropped">Events lost: the queue was full, retries ran out, or the server refused the batch for good.</param>
/// <param name="Retries">How many times a batch was sent again.</param>
public sealed record EventTrackerStats(long Tracked, long Sent, long RejectedByServer, long Dropped, long Retries);
