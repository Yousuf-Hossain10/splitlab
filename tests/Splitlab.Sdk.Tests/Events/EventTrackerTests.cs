using System.Diagnostics;
using Splitlab.Sdk.Events;

namespace Splitlab.Sdk.Tests.Events;

public class EventTrackerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static EventTracker Create(FakeTransport transport, EventTrackerOptions? options = null, FixedClock? clock = null) =>
        new(transport, options ?? Options.Fast(), clock ?? new FixedClock(Noon), () => 0);

    // ---- what is sent ----

    [Fact]
    public async Task ExposuresAndConversionsAreSentWithTheirFields()
    {
        var transport = new FakeTransport();
        var clock = new FixedClock(Noon.AddTicks(1_239_999)); // 12:00:00.1239999
        await using EventTracker tracker = Create(transport, clock: clock);

        Assert.True(tracker.TrackExposure("checkout", "treatment", "user-1"));
        Assert.True(tracker.TrackConversion("purchase", "user-1", 19.99));
        Assert.True(tracker.TrackConversion("signup", "user-2"));
        await tracker.FlushAsync();

        List<TrackedEvent> sent = Assert.Single(transport.Batches);
        Assert.Equal(3, sent.Count);
        Assert.Equal(("exposure", "checkout", "treatment", null, null, "user-1"), (sent[0].Type, sent[0].ExperimentKey, sent[0].VariationKey, sent[0].Name, sent[0].Value, sent[0].UserId));
        Assert.Equal(("conversion", null, null, "purchase", 19.99, "user-1"), (sent[1].Type, sent[1].ExperimentKey, sent[1].VariationKey, sent[1].Name, sent[1].Value, sent[1].UserId));
        Assert.Null(sent[2].Value);
    }

    [Fact]
    public async Task EveryEventGetsAUniqueIdAndAMillisecondUtcTimestamp()
    {
        var transport = new FakeTransport();
        var clock = new FixedClock(Noon.ToOffset(TimeSpan.FromHours(2)).AddTicks(1_239_999));
        await using EventTracker tracker = Create(transport, clock: clock);

        for (int i = 0; i < 50; i++)
        {
            tracker.TrackExposure("checkout", "control", $"user-{i}");
        }

        await tracker.FlushAsync();

        List<TrackedEvent> sent = transport.AllEvents.ToList();
        Assert.Equal(50, sent.Select(e => e.EventId).Distinct().Count());
        Assert.All(sent, e => Assert.Matches("^[0-9a-f]{32}$", e.EventId));
        Assert.All(sent, e => Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset));
        Assert.All(sent, e => Assert.Equal(Noon.AddMilliseconds(123), e.Timestamp)); // truncated, not rounded
    }

    [Fact]
    public async Task AnAssignedDecisionIsTrackedAsAnExposure()
    {
        var transport = new FakeTransport();
        await using EventTracker tracker = Create(transport);

        bool tracked = tracker.TrackExposure(new Decision("checkout", "treatment", DecisionReason.Assigned), "user-1");
        await tracker.FlushAsync();

        Assert.True(tracked);
        TrackedEvent sent = Assert.Single(transport.AllEvents);
        Assert.Equal(("exposure", "checkout", "treatment"), (sent.Type, sent.ExperimentKey, sent.VariationKey));
    }

    [Theory]
    [InlineData(DecisionReason.NotInExperiment)]
    [InlineData(DecisionReason.ExperimentNotFound)]
    [InlineData(DecisionReason.ConfigNotLoaded)]
    public async Task ADefaultServedInsteadOfAnAssignmentIsNotAnExposure(DecisionReason reason)
    {
        var transport = new FakeTransport();
        await using EventTracker tracker = Create(transport);

        bool tracked = tracker.TrackExposure(new Decision("checkout", "control", reason), "user-1");
        await tracker.FlushAsync();

        Assert.False(tracked);
        Assert.Equal(0, transport.Calls); // showing the fallback is not the user seeing a variation
        Assert.Equal(0, tracker.Stats.Tracked);
    }

    [Fact]
    public async Task EmptyArgumentsAreRejectedImmediately()
    {
        await using EventTracker tracker = Create(new FakeTransport());

        Assert.Throws<ArgumentException>(() => tracker.TrackExposure("", "treatment", "u"));
        Assert.Throws<ArgumentException>(() => tracker.TrackExposure("checkout", "", "u"));
        Assert.Throws<ArgumentException>(() => tracker.TrackExposure("checkout", "treatment", ""));
        Assert.Throws<ArgumentException>(() => tracker.TrackConversion("", "u"));
        Assert.Throws<ArgumentNullException>(() => tracker.TrackExposure(null!, "u"));
    }

    // ---- batching ----

    [Fact]
    public async Task BatchesNeverExceedTheConfiguredSize()
    {
        var transport = new FakeTransport();
        await using EventTracker tracker = Create(transport, Options.Fast(maxBatchSize: 100, flushInterval: TimeSpan.FromSeconds(10)));

        for (int i = 0; i < 250; i++)
        {
            tracker.TrackExposure("checkout", "control", $"u{i}");
        }

        await tracker.FlushAsync();

        Assert.Equal([100, 100, 50], transport.Batches.Select(b => b.Count));
        Assert.Equal(250, transport.AllEvents.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task APartialBatchIsSentAfterTheFlushIntervalWithoutAskingForIt()
    {
        var transport = new FakeTransport();
        await using EventTracker tracker = Create(transport, Options.Fast(flushInterval: TimeSpan.FromMilliseconds(50)));

        tracker.TrackExposure("checkout", "control", "u1");
        tracker.TrackExposure("checkout", "control", "u2");

        await Wait.UntilAsync(() => transport.Calls == 1, "the interval flush");
        Assert.Equal(2, transport.Batches[0].Count);
    }

    [Fact]
    public async Task FlushOnlyCoversEventsTrackedBeforeIt()
    {
        var transport = new FakeTransport();
        await using EventTracker tracker = Create(transport, Options.Fast(flushInterval: TimeSpan.FromSeconds(10)));
        tracker.TrackExposure("checkout", "control", "before");

        await tracker.FlushAsync();
        tracker.TrackExposure("checkout", "control", "after");

        Assert.Equal(["before"], transport.AllEvents.Select(e => e.UserId));
    }

    [Fact]
    public async Task ManyThreadsCanTrackAtOnceAndEveryEventIsSentExactlyOnce()
    {
        var transport = new FakeTransport();
        await using EventTracker tracker = Create(transport, Options.Fast(flushInterval: TimeSpan.FromMilliseconds(20)));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                tracker.TrackExposure("checkout", "control", $"t{t}-{i}");
            }
        })));
        await tracker.FlushAsync();

        Assert.Equal(4_000, transport.AllEvents.Select(e => e.EventId).Distinct().Count());
        Assert.Equal(4_000, transport.AllEvents.Count());
        Assert.Equal(new EventTrackerStats(4_000, 4_000, 0, 0, 0), tracker.Stats);
    }

    // ---- retrying ----

    [Fact]
    public async Task ARetryResendsExactlyTheSameEvents()
    {
        // The server deduplicates on event id and timestamp, so a retry must not change either.
        var transport = new FakeTransport()
            .Then(EventSendResult.Transient("blip"))
            .Then(EventSendResult.Transient("blip"));
        var clock = new FixedClock(Noon);
        await using EventTracker tracker = Create(transport, clock: clock);

        tracker.TrackExposure("checkout", "control", "u1");
        clock.Now = Noon.AddMinutes(5); // time passes while it is retried
        tracker.TrackConversion("purchase", "u1", 5);
        await tracker.FlushAsync();

        Assert.Equal(3, transport.Calls);
        Assert.Equal(transport.Batches[0], transport.Batches[1]);
        Assert.Equal(transport.Batches[0], transport.Batches[2]);
        Assert.Equal(new EventTrackerStats(2, 2, 0, 0, 2), tracker.Stats);
    }

    [Fact]
    public async Task ThrottlingIsRetriedAndTheServersRetryAfterIsHonoured()
    {
        var transport = new FakeTransport().Then(EventSendResult.Throttled(TimeSpan.FromMilliseconds(150)));
        await using EventTracker tracker = Create(transport, Options.Fast(maxBackoff: TimeSpan.FromSeconds(5)));

        tracker.TrackExposure("checkout", "control", "u1");
        await tracker.FlushAsync();

        Assert.Equal(2, transport.Calls);
        TimeSpan gap = transport.CallTimes[1] - transport.CallTimes[0];
        Assert.True(gap >= TimeSpan.FromMilliseconds(140), $"waited only {gap.TotalMilliseconds:F0} ms, but the server asked for 150");
        Assert.Equal(1, tracker.Stats.Sent);
    }

    [Fact]
    public async Task AFlushWaitsForTheRetriesToFinish()
    {
        var transport = new FakeTransport().Then(EventSendResult.Transient()).Then(EventSendResult.Transient());
        await using EventTracker tracker = Create(transport);
        tracker.TrackExposure("checkout", "control", "u1");

        await tracker.FlushAsync();

        Assert.Equal(1, tracker.Stats.Sent); // delivered by the time FlushAsync returns, not merely attempted
    }

    [Fact]
    public async Task GivesUpOnABatchAfterTheMaximumAttemptsButKeepsGoing()
    {
        var problems = new List<EventTrackerProblem>();
        var transport = new FakeTransport().Repeat(EventSendResult.Transient("down"), 3);
        await using EventTracker tracker = Create(transport, Options.Fast(maxAttempts: 3, onProblem: problems.Add));

        tracker.TrackExposure("checkout", "control", "lost-1");
        tracker.TrackExposure("checkout", "control", "lost-2");
        await tracker.FlushAsync();
        tracker.TrackExposure("checkout", "control", "later");
        await tracker.FlushAsync();

        Assert.Equal(4, transport.Calls); // 3 failed attempts, then the later batch
        Assert.Equal(new EventTrackerStats(3, 1, 0, 2, 2), tracker.Stats);
        EventTrackerProblem problem = Assert.Single(problems);
        Assert.Equal((EventTrackerProblemKind.BatchDropped, 2), (problem.Kind, problem.EventCount));
        Assert.Contains("3 attempts", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermanentFailureIsNotRetried()
    {
        var problems = new List<EventTrackerProblem>();
        var transport = new FakeTransport().Then(EventSendResult.Permanent("HTTP 401"));
        await using EventTracker tracker = Create(transport, Options.Fast(onProblem: problems.Add));

        tracker.TrackExposure("checkout", "control", "u1");
        await tracker.FlushAsync();

        Assert.Equal(1, transport.Calls);
        Assert.Equal(new EventTrackerStats(1, 0, 0, 1, 0), tracker.Stats);
        Assert.Contains("will not help", Assert.Single(problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsTheServerRefusesAsInvalidAreCountedAndNotRetried()
    {
        var problems = new List<EventTrackerProblem>();
        var transport = new FakeTransport().Then(EventSendResult.Accepted(serverRejected: 2));
        await using EventTracker tracker = Create(transport, Options.Fast(onProblem: problems.Add));

        for (int i = 0; i < 5; i++)
        {
            tracker.TrackExposure("checkout", "control", $"u{i}");
        }

        await tracker.FlushAsync();

        Assert.Equal(1, transport.Calls);
        Assert.Equal(new EventTrackerStats(5, 3, 2, 0, 0), tracker.Stats);
        EventTrackerProblem problem = Assert.Single(problems);
        Assert.Equal((EventTrackerProblemKind.EventsRejected, 2), (problem.Kind, problem.EventCount));
    }

    [Fact]
    public async Task ATransportThatThrowsIsTreatedAsATransientFailure()
    {
        var transport = new FakeTransport().Then((_, _) => throw new InvalidOperationException("bug in a custom transport"));
        await using EventTracker tracker = Create(transport);

        tracker.TrackExposure("checkout", "control", "u1");
        await tracker.FlushAsync(); // does not throw

        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, tracker.Stats.Sent);
    }

    [Fact]
    public async Task AFaultyProblemCallbackCannotStopDelivery()
    {
        var transport = new FakeTransport().Then(EventSendResult.Permanent("HTTP 401"));
        await using EventTracker tracker = Create(transport, Options.Fast(onProblem: _ => throw new InvalidOperationException("logging is broken")));

        tracker.TrackExposure("checkout", "control", "first");
        await tracker.FlushAsync();
        tracker.TrackExposure("checkout", "control", "second");
        await tracker.FlushAsync();

        Assert.Equal(1, tracker.Stats.Sent);
    }

    // ---- backpressure and shutdown ----

    [Fact]
    public async Task WhenTheQueueIsFullNewEventsAreDroppedAndCountedInsteadOfGrowingMemory()
    {
        var gate = new TaskCompletionSource();
        var transport = new FakeTransport().Then(async (_, _) =>
        {
            await gate.Task;
            return EventSendResult.Accepted();
        });
        await using EventTracker tracker = Create(transport, Options.Fast(maxBatchSize: 1, queueCapacity: 3));

        tracker.TrackExposure("checkout", "control", "in-flight"); // taken by the sender, which is now stuck
        await Wait.UntilAsync(() => transport.Calls == 1, "the first send to start");

        Assert.True(tracker.TrackExposure("checkout", "control", "queued-1"));
        Assert.True(tracker.TrackExposure("checkout", "control", "queued-2"));
        Assert.True(tracker.TrackExposure("checkout", "control", "queued-3"));
        Assert.False(tracker.TrackExposure("checkout", "control", "overflow"));
        Assert.Equal(1, tracker.Stats.Dropped);

        gate.SetResult();
        await tracker.FlushAsync();

        Assert.Equal(new EventTrackerStats(5, 4, 0, 1, 0), tracker.Stats);
        Assert.DoesNotContain(transport.AllEvents, e => e.UserId == "overflow");
    }

    [Fact]
    public async Task TrackingNeverWaitsForTheNetwork()
    {
        var transport = new FakeTransport().Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // a transport that hangs forever
            return EventSendResult.Accepted();
        });
        var tracker = Create(transport, Options.Fast(shutdownTimeout: TimeSpan.FromMilliseconds(100)));

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 1_000; i++)
        {
            tracker.TrackExposure("checkout", "control", $"u{i}");
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"1,000 calls took {clock.ElapsedMilliseconds} ms");
        await tracker.DisposeAsync();
    }

    [Fact]
    public async Task DisposingSendsWhatIsStillQueuedAndThenStopsAcceptingEvents()
    {
        var transport = new FakeTransport();
        var tracker = Create(transport, Options.Fast(flushInterval: TimeSpan.FromMinutes(5)));
        for (int i = 0; i < 5; i++)
        {
            tracker.TrackExposure("checkout", "control", $"u{i}");
        }

        await tracker.DisposeAsync();

        Assert.Equal(5, transport.AllEvents.Count());
        Assert.Equal(5, tracker.Stats.Sent);
        Assert.False(tracker.TrackExposure("checkout", "control", "too-late"));
        Assert.Equal(1, tracker.Stats.Dropped);
    }

    [Fact]
    public async Task DisposingGivesUpOnAStuckBatchAfterTheShutdownTimeout()
    {
        var problems = new List<EventTrackerProblem>();
        var transport = new FakeTransport().Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return EventSendResult.Accepted();
        });
        var tracker = Create(transport, Options.Fast(shutdownTimeout: TimeSpan.FromMilliseconds(150), onProblem: problems.Add));
        tracker.TrackExposure("checkout", "control", "u1");
        tracker.TrackExposure("checkout", "control", "u2");
        await Wait.UntilAsync(() => transport.Calls == 1, "the send to start");

        var clock = Stopwatch.StartNew();
        await tracker.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"disposing took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(2, tracker.Stats.Dropped);
        Assert.Contains("Shut down", Assert.Single(problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlushAfterDisposeReturnsInsteadOfHanging()
    {
        var tracker = Create(new FakeTransport());
        await tracker.DisposeAsync();

        await tracker.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        var tracker = Create(new FakeTransport());

        await tracker.DisposeAsync();
        await tracker.DisposeAsync();
        tracker.Dispose();
    }

    [Fact]
    public async Task AFlushCanBeCancelled()
    {
        var transport = new FakeTransport().Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return EventSendResult.Accepted();
        });
        var tracker = Create(transport, Options.Fast(shutdownTimeout: TimeSpan.FromMilliseconds(100)));
        tracker.TrackExposure("checkout", "control", "u1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracker.FlushAsync(cts.Token));
        await tracker.DisposeAsync();
    }

    // ---- backoff arithmetic ----

    [Fact]
    public async Task BackoffDoublesAndIsCapped()
    {
        var options = new EventTrackerOptions { InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(8), JitterFraction = 0 };
        await using var tracker = new EventTracker(new FakeTransport(), options, TimeProvider.System, () => 0);

        Assert.Equal(
            [1, 2, 4, 8, 8, 8],
            Enumerable.Range(1, 6).Select(attempt => tracker.BackoffDelay(attempt, null).TotalSeconds));
        Assert.Equal(8, tracker.BackoffDelay(1_000, null).TotalSeconds);
    }

    [Fact]
    public async Task TheServersRetryAfterRaisesTheDelayButIsAlsoCapped()
    {
        var options = new EventTrackerOptions { InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(8), JitterFraction = 0 };
        await using var tracker = new EventTracker(new FakeTransport(), options, TimeProvider.System, () => 0);

        Assert.Equal(5, tracker.BackoffDelay(1, TimeSpan.FromSeconds(5)).TotalSeconds); // longer than our own 1 s
        Assert.Equal(4, tracker.BackoffDelay(3, TimeSpan.FromSeconds(1)).TotalSeconds); // shorter than our own 4 s: ours stands
        Assert.Equal(8, tracker.BackoffDelay(1, TimeSpan.FromHours(1)).TotalSeconds); // an absurd request does not stall the queue
    }

    [Fact]
    public async Task JitterSpreadsRetriesOutButNeverShortensThem()
    {
        var options = new EventTrackerOptions { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromMinutes(5), JitterFraction = 0.2 };
        await using var low = new EventTracker(new FakeTransport(), options, TimeProvider.System, () => 0);
        await using var high = new EventTracker(new FakeTransport(), options, TimeProvider.System, () => 1);

        Assert.Equal(10, low.BackoffDelay(1, null).TotalSeconds);
        Assert.Equal(12, high.BackoffDelay(1, null).TotalSeconds);
    }

    // ---- configuration ----

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(-1)]
    public void TheBatchSizeMustBeBetweenOneAndTheServerLimit(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(new FakeTransport(), new EventTrackerOptions { MaxBatchSize = size }));
    }

    [Fact]
    public void NonsenseSettingsAreRejected()
    {
        var transport = new FakeTransport();

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(transport, new EventTrackerOptions { QueueCapacity = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(transport, new EventTrackerOptions { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(transport, new EventTrackerOptions { FlushInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(transport, new EventTrackerOptions { InitialBackoff = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(
            transport, new EventTrackerOptions { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new EventTracker(transport, new EventTrackerOptions { JitterFraction = -0.1 }));
        Assert.Throws<ArgumentNullException>(() => _ = new EventTracker(null!));
    }
}
