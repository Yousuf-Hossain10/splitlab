using Splitlab.Sdk.Bucketing;
using Splitlab.Sdk.Config;

namespace Splitlab.Sdk;

/// <summary>
/// Makes local decisions from a datafile that is refreshed in the background (ADR-0007).
/// Decisions never perform I/O or take locks: they read an immutable snapshot that is swapped atomically.
/// </summary>
public sealed class SplitlabClient : IDisposable
{
    private readonly IDatafileSource _source;
    private readonly SplitlabClientOptions _options;
    private readonly Func<double> _nextRandom;
    private readonly CancellationTokenSource _stop = new();
    private ConfigSnapshot? _snapshot;
    private int _started;

    /// <summary>Creates a client over a datafile source.</summary>
    /// <exception cref="DatafileException">The embedded datafile is invalid.</exception>
    public SplitlabClient(IDatafileSource source, SplitlabClientOptions? options = null)
        : this(source, options, Random.Shared.NextDouble)
    {
    }

    internal SplitlabClient(IDatafileSource source, SplitlabClientOptions? options, Func<double> nextRandom)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _options = options ?? new SplitlabClientOptions();
        _nextRandom = nextRandom;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.JitterFraction);

        if (_options.EmbeddedDatafile is not null)
        {
            _snapshot = DatafileParser.Parse(_options.EmbeddedDatafile);
        }
    }

    /// <summary>The configuration decisions are currently made against, or <c>null</c> if none has loaded.</summary>
    public ConfigSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Decides which variation a user sees. Never throws for missing configuration:
    /// the result carries <paramref name="defaultVariation"/> and a <see cref="DecisionReason"/> instead.
    /// </summary>
    public Decision Decide(string experimentKey, string bucketingId, string defaultVariation)
    {
        ArgumentException.ThrowIfNullOrEmpty(experimentKey);
        ArgumentException.ThrowIfNullOrEmpty(bucketingId);
        ArgumentNullException.ThrowIfNull(defaultVariation);

        ConfigSnapshot? snapshot = Snapshot;
        if (snapshot is null)
        {
            return new Decision(experimentKey, defaultVariation, DecisionReason.ConfigNotLoaded);
        }

        if (!snapshot.TryGetExperiment(experimentKey, out Experiment? experiment))
        {
            return new Decision(experimentKey, defaultVariation, DecisionReason.ExperimentNotFound);
        }

        string? variation = Bucketer.Assign(bucketingId, experiment);
        return variation is null
            ? new Decision(experimentKey, defaultVariation, DecisionReason.NotInExperiment)
            : new Decision(experimentKey, variation, DecisionReason.Assigned);
    }

    /// <summary>
    /// Fetches the datafile once and swaps in the new snapshot if it changed.
    /// Failures leave the last known good snapshot in place and are rethrown to the caller.
    /// </summary>
    /// <returns><c>true</c> if a new snapshot was installed.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ConfigSnapshot? current = Snapshot;
        DatafileFetchResult result = await _source
            .FetchAsync(current?.ETag, cancellationToken)
            .ConfigureAwait(false);

        if (result.NotModified)
        {
            return false;
        }

        ConfigSnapshot next = DatafileParser.Parse(
            result.Json ?? throw new DatafileException("The server returned an empty datafile."),
            result.ETag);
        Volatile.Write(ref _snapshot, next);
        return true;
    }

    /// <summary>
    /// Starts background polling. The first fetch happens immediately; failures are swallowed and retried
    /// with exponential backoff while the last known good snapshot keeps serving decisions.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            CancellationToken token = _stop.Token;
            _ = Task.Run(() => PollAsync(token), token);
        }
    }

    /// <summary>Delay before the next poll, given the number of consecutive failures so far.</summary>
    internal TimeSpan NextDelay(int consecutiveFailures)
    {
        TimeSpan delay = _options.PollInterval;
        if (consecutiveFailures > 0)
        {
            double scaled = delay.TotalMilliseconds * Math.Pow(2, Math.Min(consecutiveFailures, 30));
            double capped = Math.Min(scaled, _options.MaxBackoff.TotalMilliseconds);
            delay = TimeSpan.FromMilliseconds(Math.Max(capped, delay.TotalMilliseconds));
        }

        return delay + (delay * _options.JitterFraction * _nextRandom());
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or DatafileException or OperationCanceledException)
            {
                failures++;
            }

            try
            {
                await Task.Delay(NextDelay(failures), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Stops background polling.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
