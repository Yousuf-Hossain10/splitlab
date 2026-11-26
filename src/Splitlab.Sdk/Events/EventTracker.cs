using System.Threading.Channels;

namespace Splitlab.Sdk.Events;

/// <summary>
/// Reports exposures and conversions to Splitlab without ever blocking or failing the application (ADR-0005).
/// <list type="bullet">
/// <item>Tracking only queues the event in memory; a background loop sends batches.</item>
/// <item>A batch that fails is sent again <b>unchanged</b>, with the same event ids and timestamps, so the server stores each event once.</item>
/// <item>Throttling (HTTP 429) and server errors are retried with exponential backoff, jitter and the server's <c>Retry-After</c>.</item>
/// <item>The queue is bounded: when the server cannot keep up, the newest events are dropped and counted, never memory.</item>
/// </list>
/// Delivery is at-least-once, effectively once after the server deduplicates. Events still queued when the process
/// crashes are lost, which is acceptable for analytics counts.
/// </summary>
public sealed class EventTracker : IAsyncDisposable, IDisposable
{
    private readonly IEventTransport _transport;
    private readonly EventTrackerOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<double> _nextRandom;
    private readonly Channel<Item> _queue;
    private readonly CancellationTokenSource _hardStop = new();
    private readonly Task _loop;
    private int _disposed;

    private long _tracked;
    private long _sent;
    private long _rejected;
    private long _dropped;
    private long _retries;

    /// <summary>Creates a tracker that starts sending in the background immediately.</summary>
    public EventTracker(IEventTransport transport, EventTrackerOptions? options = null)
        : this(transport, options, TimeProvider.System, Random.Shared.NextDouble)
    {
    }

    internal EventTracker(IEventTransport transport, EventTrackerOptions? options, TimeProvider clock, Func<double> nextRandom)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _options = options ?? new EventTrackerOptions();
        _clock = clock;
        _nextRandom = nextRandom;

        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxBatchSize, 500);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.QueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.FlushInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.InitialBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxBackoff, _options.InitialBackoff);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.JitterFraction);

        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Counters for what has happened so far. Safe to read at any time.</summary>
    public EventTrackerStats Stats => new(
        Interlocked.Read(ref _tracked),
        Interlocked.Read(ref _sent),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _retries));

    /// <summary>Records that <paramref name="userId"/> was shown <paramref name="variationKey"/> of an experiment.</summary>
    /// <returns><c>true</c> if the event was queued; <c>false</c> if it was dropped because the queue is full or the tracker is disposed.</returns>
    public bool TrackExposure(string experimentKey, string variationKey, string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(experimentKey);
        ArgumentException.ThrowIfNullOrEmpty(variationKey);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        return Enqueue(experimentKey, variationKey, null, null, userId, TrackedEvent.Exposure);
    }

    /// <summary>
    /// Records an exposure for a decision, but only if the user really was assigned a variation. A default served because
    /// nothing was loaded, or because the user is outside the experiment's traffic, is not an exposure.
    /// </summary>
    /// <returns><c>true</c> if an exposure was queued.</returns>
    public bool TrackExposure(Decision decision, string userId)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        return decision.IsAssigned && TrackExposure(decision.ExperimentKey, decision.VariationKey, userId);
    }

    /// <summary>Records that <paramref name="userId"/> did something being measured, such as <c>purchase</c>.</summary>
    /// <param name="metric">The conversion's name; results are asked for by this name.</param>
    /// <param name="userId">The same user id that was used for bucketing.</param>
    /// <param name="value">An optional number, such as revenue.</param>
    /// <returns><c>true</c> if the event was queued; <c>false</c> if it was dropped because the queue is full or the tracker is disposed.</returns>
    public bool TrackConversion(string metric, string userId, double? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(metric);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        return Enqueue(null, null, metric, value, userId, TrackedEvent.Conversion);
    }

    /// <summary>
    /// Sends everything tracked before this call and waits until it has been delivered or given up on. Normally unnecessary,
    /// because events are sent in the background; useful before a short-lived process exits and in tests.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Queued behind the events already waiting, so it is reached only after all of them have been handled.
            await _queue.Writer.WriteAsync(new Item(null, done), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return; // already shut down: nothing is left to flush
        }

        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops accepting events, then tries to send what is queued for up to <see cref="EventTrackerOptions.ShutdownTimeout"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Out of time: abandon retries and count what is left as dropped.
            await _hardStop.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
        }

        _hardStop.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>How long to wait before retry number <paramref name="attempt"/> (1 for the first retry).</summary>
    internal TimeSpan BackoffDelay(int attempt, TimeSpan? retryAfter)
    {
        double scaled = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        TimeSpan delay = TimeSpan.FromMilliseconds(scaled);

        // When the server says how long to wait, never come back sooner than that...
        if (retryAfter is { } asked && asked > delay)
        {
            delay = asked;
        }

        // ...but never wait longer than the cap either: a server asking for an hour would otherwise stall the queue
        // until it fills and starts dropping events.
        if (delay > _options.MaxBackoff)
        {
            delay = _options.MaxBackoff;
        }

        return delay + (delay * _options.JitterFraction * _nextRandom());
    }

    private bool Enqueue(string? experimentKey, string? variationKey, string? name, double? value, string userId, string type)
    {
        Interlocked.Increment(ref _tracked);

        // Whole milliseconds, because that is the precision the server deduplicates on.
        long ticks = _clock.GetUtcNow().UtcTicks;
        var timestamp = new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        var tracked = new TrackedEvent(Guid.NewGuid().ToString("N"), type, experimentKey, variationKey, name, value, userId, timestamp);

        if (Volatile.Read(ref _disposed) == 0 && _queue.Writer.TryWrite(new Item(tracked, null)))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private async Task RunAsync()
    {
        CancellationToken stop = _hardStop.Token;
        var markers = new List<TaskCompletionSource>();

        try
        {
            // Not tied to the hard stop: after Dispose the loop keeps going until the queue is empty, or it is cancelled.
            while (await _queue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var batch = new List<TrackedEvent>(_options.MaxBatchSize);
                using var window = new CancellationTokenSource(_options.FlushInterval);

                while (batch.Count < _options.MaxBatchSize)
                {
                    if (_queue.Reader.TryRead(out Item item))
                    {
                        if (item.Event is not null)
                        {
                            batch.Add(item.Event);
                            continue;
                        }

                        markers.Add(item.Marker!);
                        break; // a flush was asked for: send what came before it now
                    }

                    try
                    {
                        if (!await _queue.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break; // the flush interval elapsed
                    }
                }

                if (batch.Count > 0)
                {
                    await SendWithRetriesAsync(batch, stop).ConfigureAwait(false);
                }

                foreach (TaskCompletionSource marker in markers)
                {
                    marker.TrySetResult();
                }

                markers.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Hard stop: fall through to account for whatever was left.
        }
        finally
        {
            while (_queue.Reader.TryRead(out Item item))
            {
                if (item.Event is not null)
                {
                    Interlocked.Increment(ref _dropped);
                }
                else
                {
                    item.Marker!.TrySetResult();
                }
            }

            foreach (TaskCompletionSource marker in markers)
            {
                marker.TrySetResult(); // never leave a FlushAsync hanging
            }
        }
    }

    private async Task SendWithRetriesAsync(List<TrackedEvent> batch, CancellationToken stop)
    {
        for (int attempt = 1; ; attempt++)
        {
            EventSendResult result;
            try
            {
                result = await _transport.SendAsync(batch, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Drop(batch.Count, "Shut down before the batch could be delivered.");
                throw;
            }
            catch (Exception ex)
            {
                // A transport that throws is treated like a network failure; the application must never see it.
                result = EventSendResult.Transient(ex.Message);
            }

            switch (result.Status)
            {
                case EventSendStatus.Accepted:
                    Interlocked.Add(ref _sent, batch.Count - result.ServerRejected);
                    if (result.ServerRejected > 0)
                    {
                        Interlocked.Add(ref _rejected, result.ServerRejected);
                        Report(EventTrackerProblemKind.EventsRejected, $"The server refused {result.ServerRejected} invalid event(s).", result.ServerRejected);
                    }

                    return;

                case EventSendStatus.Permanent:
                    Drop(batch.Count, $"The server refused the batch and retrying will not help ({result.Detail}).");
                    return;

                default:
                    if (attempt >= _options.MaxAttempts)
                    {
                        Drop(batch.Count, $"Gave up after {attempt} attempts ({result.Detail ?? result.Status.ToString()}).");
                        return;
                    }

                    Interlocked.Increment(ref _retries);
                    try
                    {
                        await Task.Delay(BackoffDelay(attempt, result.RetryAfter), stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Drop(batch.Count, "Shut down before the batch could be delivered.");
                        throw;
                    }

                    break;
            }
        }
    }

    private void Drop(int count, string reason)
    {
        Interlocked.Add(ref _dropped, count);
        Report(EventTrackerProblemKind.BatchDropped, reason, count);
    }

    private void Report(EventTrackerProblemKind kind, string message, int count)
    {
        try
        {
            _options.OnProblem?.Invoke(new EventTrackerProblem(kind, message, count));
        }
        catch (Exception)
        {
            // A faulty callback must not stop delivery.
        }
    }

    /// <summary>A queue entry: an event to send, or a request to flush everything before it.</summary>
    private readonly record struct Item(TrackedEvent? Event, TaskCompletionSource? Marker);
}
