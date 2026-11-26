using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Infrastructure.Persistence;
using Splitlab.Sdk;
using Splitlab.Sdk.Config;
using Splitlab.Sdk.Events;

namespace Splitlab.Api.Tests;

/// <summary>
/// The real SDK against the real API: it fetches the datafile, makes decisions locally, reports exposures and conversions
/// with its key, and the results show exactly what it reported.
/// </summary>
public sealed class SdkIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri BaseUri = new("http://localhost/");

    /// <summary>Forwards each request to the real server, but for the first few event batches throws the answer away and reports an outage.</summary>
    private sealed class LoseFirstEventResponses(int count) : DelegatingHandler
    {
        private int _lost;

        public int EventPosts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/events")
            {
                EventPosts++;
                if (Interlocked.Increment(ref _lost) <= count)
                {
                    // The server has already stored these events; the SDK just never hears about it.
                    response.Dispose();
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
            }

            return response;
        }
    }

    private static EventTrackerOptions Fast(Action<EventTrackerProblem>? onProblem = null) => new()
    {
        FlushInterval = TimeSpan.FromMilliseconds(30),
        InitialBackoff = TimeSpan.FromMilliseconds(5),
        MaxBackoff = TimeSpan.FromMilliseconds(40),
        JitterFraction = 0,
        OnProblem = onProblem,
    };

    private async Task<(Workspace Workspace, IssuedKey Key)> RunningExperimentAsync()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        ExperimentView created = await ws.CreateExperimentAsync();
        (await ws.Client.PostAsJsonAsync($"{ws.EnvironmentUrl}/experiments/checkout/start", new { version = created.Version })).EnsureSuccessStatusCode();
        return (ws, await ws.CreateKeyAsync("config:read", "events:write"));
    }

    /// <summary>
    /// The number of event rows actually stored for the environment. Results count unique users, so they would look the same
    /// even if events were stored several times; only the rows themselves show whether a retry was deduplicated.
    /// </summary>
    private async Task<int> StoredEventRowsAsync(Workspace ws)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        Guid environment = await db.Environments.IgnoreQueryFilters().Where(e => e.Key == ws.Environment).Select(e => e.Id).SingleAsync();
        return await db.Events.IgnoreQueryFilters().CountAsync(e => e.EnvironmentId == environment);
    }

    private static async Task<Dictionary<string, (long Exposed, long Converted)>> ResultsAsync(Workspace ws)
    {
        string json = await ws.Client.GetStringAsync($"{ws.EnvironmentUrl}/experiments/checkout/results?metric=purchase");
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("results").GetProperty("analysis").GetProperty("variations").EnumerateArray()
            .ToDictionary(v => v.GetProperty("key").GetString()!, v => (v.GetProperty("exposed").GetInt64(), v.GetProperty("converted").GetInt64()));
    }

    private static async Task<Dictionary<string, (long Exposed, long Converted)>> WaitForExposuresAsync(Workspace ws, long expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        Dictionary<string, (long Exposed, long Converted)> results;
        do
        {
            results = await ResultsAsync(ws);
            if (results.Values.Sum(v => v.Exposed) >= expected)
            {
                break;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        return results;
    }

    [Fact]
    public async Task DecisionsMadeLocallyAndReportedByTheSdkAreWhatTheResultsShow()
    {
        (Workspace ws, IssuedKey key) = await RunningExperimentAsync();
        HttpClient http = factory.CreateClient();
        using var client = new SplitlabClient(new HttpDatafileSource(http, BaseUri, ws.Environment, key.Secret));
        await client.RefreshAsync();
        await using var tracker = new EventTracker(new HttpEventTransport(http, BaseUri, key.Secret), Fast());

        var shown = new Dictionary<string, long> { ["control"] = 0, ["treatment"] = 0 };
        var converted = new Dictionary<string, long> { ["control"] = 0, ["treatment"] = 0 };
        for (int i = 0; i < 400; i++)
        {
            string user = $"sdk-user-{i}";
            Decision decision = client.Decide("checkout", user, "control");
            Assert.True(decision.IsAssigned);

            Assert.True(tracker.TrackExposure(decision, user));
            shown[decision.VariationKey]++;
            if (i % 5 == 0)
            {
                tracker.TrackConversion("purchase", user);
                converted[decision.VariationKey]++;
            }
        }

        await tracker.FlushAsync();

        Dictionary<string, (long Exposed, long Converted)> results = await WaitForExposuresAsync(ws, 400);
        Assert.Equal((shown["control"], converted["control"]), results["control"]);
        Assert.Equal((shown["treatment"], converted["treatment"]), results["treatment"]);
        Assert.Equal(new EventTrackerStats(480, 480, 0, 0, 0), tracker.Stats);
    }

    [Fact]
    public async Task ARetryAfterALostResponseIsStoredExactlyOnce()
    {
        (Workspace ws, IssuedKey key) = await RunningExperimentAsync();
        var lossy = new LoseFirstEventResponses(count: 2);
        HttpClient http = factory.CreateDefaultClient(BaseUri, lossy);
        await using var tracker = new EventTracker(new HttpEventTransport(http, BaseUri, key.Secret), Fast());

        for (int i = 0; i < 50; i++)
        {
            tracker.TrackExposure("checkout", i % 2 == 0 ? "control" : "treatment", $"once-{i}");
        }

        await tracker.FlushAsync();
        Dictionary<string, (long Exposed, long Converted)> results = await WaitForExposuresAsync(ws, 50);
        await Task.Delay(300); // time for any (wrongly) duplicated events to be written as well

        results = await ResultsAsync(ws);
        Assert.Equal(3, lossy.EventPosts); // the same batch reached the server three times...
        Assert.Equal(50, await StoredEventRowsAsync(ws)); // ...and was stored once (150 rows would mean duplicates)
        Assert.Equal(50, results.Values.Sum(v => v.Exposed));
        Assert.Equal(new EventTrackerStats(50, 50, 0, 0, 2), tracker.Stats);
    }

    [Fact]
    public async Task ARevokedKeyMakesTheSdkGiveUpAndSayWhy()
    {
        (Workspace ws, IssuedKey key) = await RunningExperimentAsync();
        (await ws.Client.PostAsync($"{ws.EnvironmentUrl}/api-keys/{key.Id}/revoke", null)).EnsureSuccessStatusCode();
        var problems = new List<EventTrackerProblem>();
        await using var tracker = new EventTracker(new HttpEventTransport(factory.CreateClient(), BaseUri, key.Secret), Fast(problems.Add));

        tracker.TrackExposure("checkout", "control", "u1");
        tracker.TrackExposure("checkout", "control", "u2");
        await tracker.FlushAsync();

        Assert.Equal(new EventTrackerStats(2, 0, 0, 2, 0), tracker.Stats); // no pointless retries
        EventTrackerProblem problem = Assert.Single(problems);
        Assert.Equal(EventTrackerProblemKind.BatchDropped, problem.Kind);
        Assert.Contains("401", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyWithoutPermissionToSendEventsIsRefused()
    {
        (Workspace ws, _) = await RunningExperimentAsync();
        IssuedKey readOnly = await ws.CreateKeyAsync("config:read");
        var problems = new List<EventTrackerProblem>();
        await using var tracker = new EventTracker(new HttpEventTransport(factory.CreateClient(), BaseUri, readOnly.Secret), Fast(problems.Add));

        tracker.TrackExposure("checkout", "control", "u1");
        await tracker.FlushAsync();

        Assert.Equal(1, tracker.Stats.Dropped);
        Assert.Contains("403", Assert.Single(problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsTheServerRejectsAsInvalidAreReportedNotRetried()
    {
        (Workspace ws, IssuedKey key) = await RunningExperimentAsync();
        var problems = new List<EventTrackerProblem>();
        await using var tracker = new EventTracker(new HttpEventTransport(factory.CreateClient(), BaseUri, key.Secret), Fast(problems.Add));

        tracker.TrackExposure("checkout", "control", "good-user");
        tracker.TrackExposure("Not A Valid Key", "control", "bad-user"); // the server insists on well-formed keys
        await tracker.FlushAsync();
        Dictionary<string, (long Exposed, long Converted)> results = await WaitForExposuresAsync(ws, 1);

        Assert.Equal(new EventTrackerStats(2, 1, 1, 0, 0), tracker.Stats);
        Assert.Equal(EventTrackerProblemKind.EventsRejected, Assert.Single(problems).Kind);
        Assert.Equal(1, results.Values.Sum(v => v.Exposed));
    }
}
