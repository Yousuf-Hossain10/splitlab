using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitlab.Application.Events;
using Splitlab.Domain.Events;
using Splitlab.Infrastructure.Events;

namespace Splitlab.Api.Tests;

/// <summary>The queue and the batching writer, in isolation from HTTP and the database (ADR-0005).</summary>
public sealed class EventPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static List<EventRecord> Events(int count, string prefix = "e")
    {
        var list = new List<EventRecord>();
        for (int i = 0; i < count; i++)
        {
            Assert.True(EventRecord.TryCreate(
                Guid.NewGuid(), Guid.NewGuid(), $"{prefix}-{i}", "exposure", "x", "y", null, null, "u", Now, Now, out EventRecord? e, out _));
            list.Add(e!);
        }

        return list;
    }

    private sealed class FakeStore : IEventStore
    {
        private int _failuresLeft;

        public FakeStore(int failures = 0) => _failuresLeft = failures;

        public ConcurrentQueue<int> BatchSizes { get; } = new();

        public int Attempts => _attempts;

        private int _attempts;

        public Task<int> InsertAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            if (Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw new InvalidOperationException("database down");
            }

            BatchSizes.Enqueue(batch.Count);
            return Task.FromResult(batch.Count);
        }
    }

    private static (ChannelEventSink Sink, EventWriterService Writer) Build(FakeStore store, EventPipelineOptions options)
    {
        var services = new ServiceCollection().AddSingleton<IEventStore>(store).BuildServiceProvider();
        var sink = new ChannelEventSink(options.Capacity);
        var writer = new EventWriterService(
            sink, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<EventWriterService>.Instance);
        return (sink, writer);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met within 5 seconds");
    }

    // ---- sink ----

    [Fact]
    public void SinkAcceptsUpToItsCapacityAndNoMore()
    {
        var sink = new ChannelEventSink(10);

        Assert.True(sink.TryAccept(Events(6)));
        Assert.True(sink.TryAccept(Events(4)));
        Assert.False(sink.TryAccept(Events(1)));
        Assert.Equal(10, sink.Depth);
    }

    [Fact]
    public void ABatchThatDoesNotFitIsRejectedWholeAndLeavesNoTrace()
    {
        var sink = new ChannelEventSink(10);
        Assert.True(sink.TryAccept(Events(8)));

        Assert.False(sink.TryAccept(Events(3)));

        Assert.Equal(8, sink.Depth);
        Assert.True(sink.TryAccept(Events(2))); // the rejected batch did not eat the remaining room
    }

    [Fact]
    public void ReadingFreesCapacity()
    {
        var sink = new ChannelEventSink(2);
        sink.TryAccept(Events(2));
        Assert.False(sink.TryAccept(Events(1)));

        Assert.True(sink.TryRead(out _));

        Assert.True(sink.TryAccept(Events(1)));
    }

    [Fact]
    public async Task ConcurrentProducersNeverExceedCapacity()
    {
        var sink = new ChannelEventSink(1_000);
        int accepted = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                if (sink.TryAccept(Events(1)))
                {
                    Interlocked.Increment(ref accepted);
                }
            }
        })));

        Assert.Equal(1_000, accepted);
        Assert.Equal(1_000, sink.Depth);
    }

    // ---- writer ----

    [Fact]
    public async Task FullBatchesAreFlushedAtTheBatchSize()
    {
        var store = new FakeStore();
        (ChannelEventSink sink, EventWriterService writer) = Build(
            store, new EventPipelineOptions { BatchSize = 100, FlushInterval = TimeSpan.FromSeconds(30) });
        await writer.StartAsync(default);

        sink.TryAccept(Events(250));
        await WaitUntilAsync(() => store.BatchSizes.Count >= 2);
        await writer.StopAsync(default);

        Assert.Equal([100, 100, 50], store.BatchSizes); // the remainder is drained on shutdown
    }

    [Fact]
    public async Task APartialBatchIsFlushedAfterTheInterval()
    {
        var store = new FakeStore();
        (ChannelEventSink sink, EventWriterService writer) = Build(
            store, new EventPipelineOptions { BatchSize = 500, FlushInterval = TimeSpan.FromMilliseconds(100) });
        await writer.StartAsync(default);

        sink.TryAccept(Events(3));
        await WaitUntilAsync(() => !store.BatchSizes.IsEmpty);

        Assert.Equal([3], store.BatchSizes);
        await writer.StopAsync(default);
    }

    [Fact]
    public async Task ShutdownDrainsEverythingStillQueued()
    {
        var store = new FakeStore();
        (ChannelEventSink sink, EventWriterService writer) = Build(
            store, new EventPipelineOptions { BatchSize = 500, FlushInterval = TimeSpan.FromMinutes(5) });
        await writer.StartAsync(default);
        sink.TryAccept(Events(40));

        await writer.StopAsync(default);

        Assert.Equal(40, store.BatchSizes.Sum());
        Assert.Equal(0, sink.Depth);
    }

    [Fact]
    public async Task AFailedWriteIsRetriedAndThenSucceeds()
    {
        var store = new FakeStore(failures: 2);
        (ChannelEventSink sink, EventWriterService writer) = Build(
            store, new EventPipelineOptions { FlushInterval = TimeSpan.FromMilliseconds(20), RetryDelay = TimeSpan.FromMilliseconds(5), MaxAttempts = 3 });
        await writer.StartAsync(default);

        sink.TryAccept(Events(5));
        await WaitUntilAsync(() => !store.BatchSizes.IsEmpty);
        await writer.StopAsync(default);

        Assert.Equal([5], store.BatchSizes);
        Assert.Equal(3, store.Attempts);
    }

    [Fact]
    public async Task EventsAreDroppedAndCountedWhenRetriesRunOutAndTheWriterKeepsGoing()
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineMetrics.MeterName && instrument.Name == "splitlab_ingest_events_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag is { Key: "outcome", Value: "dropped" })
                {
                    Interlocked.Add(ref dropped, value);
                }
            }
        });
        listener.Start();

        var store = new FakeStore(failures: 3); // exactly one batch's worth of attempts
        (ChannelEventSink sink, EventWriterService writer) = Build(
            store, new EventPipelineOptions { BatchSize = 4, FlushInterval = TimeSpan.FromMilliseconds(20), RetryDelay = TimeSpan.FromMilliseconds(1), MaxAttempts = 3 });
        await writer.StartAsync(default);

        sink.TryAccept(Events(4, "lost"));
        await WaitUntilAsync(() => store.Attempts >= 3);
        sink.TryAccept(Events(4, "kept"));
        await WaitUntilAsync(() => !store.BatchSizes.IsEmpty);
        await writer.StopAsync(default);

        Assert.Equal([4], store.BatchSizes);
        Assert.Equal(4, Interlocked.Read(ref dropped));
    }
}
