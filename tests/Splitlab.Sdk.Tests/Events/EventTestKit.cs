using System.Collections.Concurrent;
using Splitlab.Sdk.Events;

namespace Splitlab.Sdk.Tests.Events;

/// <summary>A clock the test controls.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// An <see cref="IEventTransport"/> that records every batch it is given and answers from a script, so tests can make
/// delivery succeed, fail, throttle or hang exactly when they want.
/// </summary>
internal sealed class FakeTransport : IEventTransport
{
    private readonly ConcurrentQueue<Func<IReadOnlyList<TrackedEvent>, CancellationToken, Task<EventSendResult>>> _script = new();
    private readonly List<(DateTimeOffset At, List<TrackedEvent> Batch)> _calls = [];
    private readonly object _lock = new();

    /// <summary>Answers the next call (calls beyond the script are accepted).</summary>
    public FakeTransport Then(EventSendResult result) => Then((_, _) => Task.FromResult(result));

    public FakeTransport Then(Func<IReadOnlyList<TrackedEvent>, CancellationToken, Task<EventSendResult>> handler)
    {
        _script.Enqueue(handler);
        return this;
    }

    public FakeTransport Repeat(EventSendResult result, int times)
    {
        for (int i = 0; i < times; i++)
        {
            Then(result);
        }

        return this;
    }

    public int Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<List<TrackedEvent>> Batches
    {
        get
        {
            lock (_lock)
            {
                return _calls.Select(c => c.Batch).ToList();
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> CallTimes
    {
        get
        {
            lock (_lock)
            {
                return _calls.Select(c => c.At).ToList();
            }
        }
    }

    public IEnumerable<TrackedEvent> AllEvents => Batches.SelectMany(b => b);

    public Task<EventSendResult> SendAsync(IReadOnlyList<TrackedEvent> batch, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((DateTimeOffset.UtcNow, batch.ToList()));
        }

        return _script.TryDequeue(out var handler)
            ? handler(batch, cancellationToken)
            : Task.FromResult(EventSendResult.Accepted());
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string because = "the condition")
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), $"{because} was not met within 5 seconds");
    }
}

internal static class Options
{
    /// <summary>Fast timings so retry tests finish quickly; every test overrides what it cares about.</summary>
    public static EventTrackerOptions Fast(
        int maxBatchSize = 500,
        int queueCapacity = 10_000,
        int maxAttempts = 6,
        TimeSpan? flushInterval = null,
        TimeSpan? maxBackoff = null,
        TimeSpan? shutdownTimeout = null,
        Action<EventTrackerProblem>? onProblem = null) => new()
    {
        MaxBatchSize = maxBatchSize,
        QueueCapacity = queueCapacity,
        MaxAttempts = maxAttempts,
        FlushInterval = flushInterval ?? TimeSpan.FromMilliseconds(30),
        InitialBackoff = TimeSpan.FromMilliseconds(5),
        MaxBackoff = maxBackoff ?? TimeSpan.FromMilliseconds(40),
        JitterFraction = 0,
        ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2),
        OnProblem = onProblem,
    };
}
