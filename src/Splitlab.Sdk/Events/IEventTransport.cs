namespace Splitlab.Sdk.Events;

/// <summary>What happened to a batch, in terms of what the sender should do next.</summary>
public enum EventSendStatus
{
    /// <summary>The server took the batch. Some individual events may still have been refused as invalid.</summary>
    Accepted,

    /// <summary>The server is full (HTTP 429). Retry the same batch after the delay it asked for.</summary>
    Throttled,

    /// <summary>A failure that may pass: a network error, a timeout or a server error. Retry the same batch.</summary>
    Transient,

    /// <summary>A failure retrying cannot fix, such as a bad or revoked key. Give the batch up.</summary>
    Permanent,
}

/// <summary>The outcome of sending one batch.</summary>
/// <param name="Status">What to do next.</param>
/// <param name="ServerRejected">When accepted, how many events the server refused as invalid (they will never be accepted).</param>
/// <param name="RetryAfter">When throttled, how long the server asked the sender to wait.</param>
/// <param name="Detail">A short explanation for diagnostics.</param>
public readonly record struct EventSendResult(
    EventSendStatus Status, int ServerRejected = 0, TimeSpan? RetryAfter = null, string? Detail = null)
{
    /// <summary>The batch was taken; <paramref name="serverRejected"/> events in it were refused as invalid.</summary>
    public static EventSendResult Accepted(int serverRejected = 0) => new(EventSendStatus.Accepted, serverRejected);

    /// <summary>The server asked the sender to slow down.</summary>
    public static EventSendResult Throttled(TimeSpan? retryAfter = null) => new(EventSendStatus.Throttled, RetryAfter: retryAfter);

    /// <summary>A failure that is worth retrying.</summary>
    public static EventSendResult Transient(string? detail = null) => new(EventSendStatus.Transient, Detail: detail);

    /// <summary>A failure that retrying cannot fix.</summary>
    public static EventSendResult Permanent(string? detail = null) => new(EventSendStatus.Permanent, Detail: detail);
}

/// <summary>Delivers batches of events to Splitlab. Abstracted so tests and other transports can substitute it.</summary>
public interface IEventTransport
{
    /// <summary>Sends one batch. Implementations report failures through the result rather than by throwing.</summary>
    Task<EventSendResult> SendAsync(IReadOnlyList<TrackedEvent> batch, CancellationToken cancellationToken);
}
