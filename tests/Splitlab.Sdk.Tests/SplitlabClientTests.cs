using Splitlab.Sdk.Config;
using Splitlab.Sdk.Tests.Config;

namespace Splitlab.Sdk.Tests;

public class SplitlabClientTests
{
    private sealed class FakeSource : IDatafileSource
    {
        private readonly Queue<Func<string?, DatafileFetchResult>> _responses = new();

        public List<string?> ETagsSeen { get; } = [];

        public void Enqueue(Func<string?, DatafileFetchResult> response) => _responses.Enqueue(response);

        public Task<DatafileFetchResult> FetchAsync(string? etag, CancellationToken cancellationToken)
        {
            ETagsSeen.Add(etag);
            return Task.FromResult(
                _responses.TryDequeue(out var response) ? response(etag) : new DatafileFetchResult(true, null, etag));
        }
    }

    private static DatafileFetchResult Ok(string json = DatafileParserTests.Valid, string etag = "\"v1\"") =>
        new(false, json, etag);

    [Fact]
    public void BeforeAnyConfigDecisionReturnsDefaultAndReason()
    {
        using var client = new SplitlabClient(new FakeSource());

        Decision decision = client.Decide("checkout_flow", "user-1", "control");

        Assert.Equal("control", decision.VariationKey);
        Assert.Equal(DecisionReason.ConfigNotLoaded, decision.Reason);
        Assert.False(decision.IsAssigned);
    }

    [Fact]
    public void EmbeddedDatafileServesDecisionsImmediately()
    {
        var options = new SplitlabClientOptions { EmbeddedDatafile = DatafileParserTests.Valid };
        using var client = new SplitlabClient(new FakeSource(), options);

        Decision decision = client.Decide("checkout_flow", "user-1", "default");

        Assert.True(decision.IsAssigned);
        Assert.True(decision.VariationKey is "control" or "treatment");
    }

    [Fact]
    public async Task RefreshInstallsSnapshotAndDecisionsMatchTheBucketer()
    {
        var source = new FakeSource();
        source.Enqueue(_ => Ok());
        using var client = new SplitlabClient(source);

        bool changed = await client.RefreshAsync();
        Decision decision = client.Decide("checkout_flow", "user-42", "default");

        Assert.True(changed);
        Assert.True(client.Snapshot!.TryGetExperiment("checkout_flow", out var experiment));
        Assert.Equal(Sdk.Bucketing.Bucketer.Assign("user-42", experiment), decision.VariationKey);
    }

    [Fact]
    public async Task UnknownExperimentReturnsDefault()
    {
        var source = new FakeSource();
        source.Enqueue(_ => Ok());
        using var client = new SplitlabClient(source);
        await client.RefreshAsync();

        Decision decision = client.Decide("nope", "user-1", "off");

        Assert.Equal("off", decision.VariationKey);
        Assert.Equal(DecisionReason.ExperimentNotFound, decision.Reason);
    }

    [Fact]
    public async Task UserOutsideAllocatedTrafficGetsDefault()
    {
        const string json = """
            { "schemaVersion": 1, "version": "1",
              "experiments": [ { "key": "tiny", "variations": [ { "key": "a", "trafficBasisPoints": 0 } ] } ] }
            """;
        var source = new FakeSource();
        source.Enqueue(_ => Ok(json));
        using var client = new SplitlabClient(source);
        await client.RefreshAsync();

        Decision decision = client.Decide("tiny", "user-1", "default");

        Assert.Equal(DecisionReason.NotInExperiment, decision.Reason);
        Assert.Equal("default", decision.VariationKey);
    }

    [Fact]
    public async Task SecondRefreshSendsETagAndTreatsNotModifiedAsSuccess()
    {
        var source = new FakeSource();
        source.Enqueue(_ => Ok());
        source.Enqueue(etag => new DatafileFetchResult(true, null, etag));
        using var client = new SplitlabClient(source);

        await client.RefreshAsync();
        ConfigSnapshot first = client.Snapshot!;
        bool changed = await client.RefreshAsync();

        Assert.False(changed);
        Assert.Equal([null, "\"v1\""], source.ETagsSeen);
        Assert.Same(first, client.Snapshot);
    }

    [Fact]
    public async Task FailedRefreshKeepsLastKnownGoodSnapshot()
    {
        var source = new FakeSource();
        source.Enqueue(_ => Ok());
        source.Enqueue(_ => throw new HttpRequestException("down"));
        source.Enqueue(_ => Ok("garbage"));
        using var client = new SplitlabClient(source);
        await client.RefreshAsync();
        ConfigSnapshot good = client.Snapshot!;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshAsync());
        await Assert.ThrowsAsync<DatafileException>(() => client.RefreshAsync());

        Assert.Same(good, client.Snapshot);
    }

    [Fact]
    public void InvalidEmbeddedDatafileFailsFast()
    {
        var options = new SplitlabClientOptions { EmbeddedDatafile = "nope" };

        Assert.Throws<DatafileException>(() => _ = new SplitlabClient(new FakeSource(), options));
    }

    [Fact]
    public void BackoffDoublesPerFailureAndIsCapped()
    {
        var options = new SplitlabClientOptions
        {
            PollInterval = TimeSpan.FromSeconds(30),
            MaxBackoff = TimeSpan.FromMinutes(5),
            JitterFraction = 0,
        };
        using var client = new SplitlabClient(new FakeSource(), options, () => 0);

        Assert.Equal(TimeSpan.FromSeconds(30), client.NextDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(60), client.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(120), client.NextDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(5), client.NextDelay(10));
        Assert.Equal(TimeSpan.FromMinutes(5), client.NextDelay(1_000));
    }

    [Fact]
    public void JitterIsAddedOnTopOfTheDelay()
    {
        var options = new SplitlabClientOptions { PollInterval = TimeSpan.FromSeconds(10), JitterFraction = 0.2 };
        using var client = new SplitlabClient(new FakeSource(), options, () => 1.0);

        Assert.Equal(TimeSpan.FromSeconds(12), client.NextDelay(0));
    }

    [Fact]
    public async Task BackgroundPollingPicksUpNewConfiguration()
    {
        var source = new FakeSource();
        source.Enqueue(_ => throw new HttpRequestException("transient"));
        source.Enqueue(_ => Ok());
        var options = new SplitlabClientOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
            JitterFraction = 0,
        };
        using var client = new SplitlabClient(source, options);

        client.Start();
        client.Start(); // idempotent

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.Snapshot is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(client.Snapshot);
    }
}
