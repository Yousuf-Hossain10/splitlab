using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Splitlab.Application.Events;
using Splitlab.Domain.Events;

namespace Splitlab.Infrastructure.Events;

public sealed class EventPipelineOptions
{
    /// <summary>Most events held in memory before the endpoint answers 429.</summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>Flush as soon as this many events are waiting.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Flush a partial batch after this long.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Attempts per batch before its events are dropped and counted.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry; doubles each attempt.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// A bounded in-process queue (ADR-0005). Batches are accepted whole or not at all: room is reserved up front, so
/// a half-accepted batch can never happen and SDK retries stay simple.
/// </summary>
public sealed class ChannelEventSink : IEventSink
{
    private readonly Channel<EventRecord> _channel;
    private readonly int _capacity;
    private int _reserved;

    public ChannelEventSink(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
        _channel = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        PipelineMetrics.RegisterQueueDepth(() => Volatile.Read(ref _reserved));
    }

    /// <summary>Events accepted and not yet picked up by the writer.</summary>
    public int Depth => Volatile.Read(ref _reserved);

    public bool TryAccept(IReadOnlyList<EventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (Interlocked.Add(ref _reserved, events.Count) > _capacity)
        {
            Interlocked.Add(ref _reserved, -events.Count);
            return false;
        }

        foreach (EventRecord e in events)
        {
            // Cannot fail: reserved <= capacity, and a slot is only released after its item was read.
            if (!_channel.Writer.TryWrite(e))
            {
                Interlocked.Decrement(ref _reserved);
                return false;
            }
        }

        return true;
    }

    internal bool TryRead(out EventRecord? record)
    {
        if (_channel.Reader.TryRead(out record))
        {
            Interlocked.Decrement(ref _reserved);
            return true;
        }

        return false;
    }

    internal ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);

    /// <summary>Stops accepting events. Already-queued events can still be read.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>
/// Drains the queue and writes events in batches: when <see cref="EventPipelineOptions.BatchSize"/> are waiting or
/// <see cref="EventPipelineOptions.FlushInterval"/> has passed. On shutdown it stops accepting and drains what is left.
/// </summary>
public sealed partial class EventWriterService : BackgroundService
{
    private readonly ChannelEventSink _sink;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<EventPipelineOptions> _options;
    private readonly ILogger<EventWriterService> _logger;

    public EventWriterService(
        ChannelEventSink sink,
        IServiceScopeFactory scopes,
        IOptions<EventPipelineOptions> options,
        ILogger<EventWriterService> logger)
    {
        _sink = sink;
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EventPipelineOptions o = _options.Value;
        var batch = new List<EventRecord>(o.BatchSize);

        // Deliberately not tied to stoppingToken: the loop ends when the sink is completed and empty, so a
        // shutdown drains the queue instead of abandoning it.
        while (await _sink.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            batch.Clear();
            using var window = new CancellationTokenSource(o.FlushInterval);

            while (batch.Count < o.BatchSize)
            {
                if (_sink.TryRead(out EventRecord? record))
                {
                    batch.Add(record!);
                    continue;
                }

                try
                {
                    if (!await _sink.WaitToReadAsync(window.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (batch.Count > 0)
            {
                await FlushAsync(batch, o).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sink.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<EventRecord> batch, EventPipelineOptions o)
    {
        long started = Stopwatch.GetTimestamp();
        TimeSpan delay = o.RetryDelay;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
                int inserted = await store.InsertAsync(batch, CancellationToken.None).ConfigureAwait(false);

                PipelineMetrics.Count("duplicate", batch.Count - inserted);
                PipelineMetrics.FlushDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= o.MaxAttempts)
                {
                    LogDropped(ex, batch.Count, attempt);
                    PipelineMetrics.Count("dropped", batch.Count);
                    return;
                }

                LogRetry(ex, batch.Count, attempt);
                await Task.Delay(delay).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropping {Count} events after {Attempts} failed write attempts.")]
    private partial void LogDropped(Exception ex, int count, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing {Count} events failed (attempt {Attempt}); retrying.")]
    private partial void LogRetry(Exception ex, int count, int attempt);
}
