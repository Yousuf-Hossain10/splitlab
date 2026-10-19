using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Application.Events;
using Splitlab.Domain.Events;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Api.Tests;

public sealed class EventsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static object Exposure(string id, string user = "user-1", DateTimeOffset? at = null) => new
    {
        eventId = id,
        type = "exposure",
        experimentKey = "checkout",
        variationKey = "control",
        userId = user,
        timestamp = at ?? DateTimeOffset.UtcNow.AddSeconds(-5),
    };

    private async Task<(Workspace Workspace, HttpClient Sdk)> SetupAsync(params string[] permissions)
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey key = await ws.CreateKeyAsync(permissions.Length == 0 ? ["events:write"] : permissions);
        return (ws, factory.ClientWithKey(key.Secret));
    }

    private async Task<List<EventRecord>> StoredAsync(string eventIdPrefix)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        return await db.Events.IgnoreQueryFilters().Where(e => e.EventId.StartsWith(eventIdPrefix)).ToListAsync();
    }

    private async Task<List<EventRecord>> WaitForStoredAsync(string prefix, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        List<EventRecord> stored;
        do
        {
            stored = await StoredAsync(prefix);
            if (stored.Count >= expected)
            {
                break;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        return stored;
    }

    private sealed record IngestResponse(int Accepted, List<RejectionView> Rejected);

    private sealed record RejectionView(int Index, string Reason);

    [Fact]
    public async Task ValidEventsAreAcceptedWithA202AndEndUpStored()
    {
        (Workspace ws, HttpClient sdk) = await SetupAsync();
        string prefix = Workspace.Unique("ok");

        HttpResponseMessage response = await sdk.PostAsJsonAsync(
            "/v1/events", new { events = new[] { Exposure($"{prefix}-1"), Exposure($"{prefix}-2", "user-2") } });
        var body = (await response.Content.ReadFromJsonAsync<IngestResponse>())!;
        List<EventRecord> stored = await WaitForStoredAsync(prefix, 2);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, body.Accepted);
        Assert.Empty(body.Rejected);
        Assert.Equal(2, stored.Count);
        Assert.Equal(ws.Project, await ProjectKeyAsync(stored[0].ProjectId));
        Assert.All(stored, e => Assert.Equal(EventKind.Exposure, e.Kind));
    }

    [Fact]
    public async Task RetriesOfTheSameEventAreStoredOnce()
    {
        (_, HttpClient sdk) = await SetupAsync();
        string prefix = Workspace.Unique("dup");
        object[] events = [Exposure($"{prefix}-1", at: DateTimeOffset.UtcNow.AddSeconds(-10))];

        await sdk.PostAsJsonAsync("/v1/events", new { events });
        await WaitForStoredAsync(prefix, 1);
        HttpResponseMessage retry = await sdk.PostAsJsonAsync("/v1/events", new { events });
        await Task.Delay(300); // long enough for the second flush

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Single(await StoredAsync(prefix));
    }

    [Fact]
    public async Task TheStoreReportsHowManyEventsWereNewAndSilentlySkipsRepeats()
    {
        var now = DateTimeOffset.UtcNow;
        string prefix = Workspace.Unique("store");
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        EventRecord Make(string id)
        {
            Assert.True(EventRecord.TryCreate(project, environment, $"{prefix}-{id}", "exposure", "x", "y", null, null, "u", now, now, out EventRecord? e, out _));
            return e!;
        }

        using IServiceScope scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        int first = await store.InsertAsync([Make("a"), Make("b")], default);
        int second = await store.InsertAsync([Make("b"), Make("c"), Make("c")], default);

        Assert.Equal(2, first);
        Assert.Equal(1, second); // "b" was stored already and "c" appeared twice in the batch
        Assert.Equal(3, (await StoredAsync(prefix)).Count);
    }

    [Fact]
    public async Task InvalidEventsAreReportedByIndexAndDoNotBlockTheValidOnes()
    {
        (_, HttpClient sdk) = await SetupAsync();
        string prefix = Workspace.Unique("mix");

        HttpResponseMessage response = await sdk.PostAsJsonAsync("/v1/events", new
        {
            events = new object[]
            {
                Exposure($"{prefix}-good"),
                new { eventId = $"{prefix}-bad", type = "nonsense", userId = "u", timestamp = DateTimeOffset.UtcNow },
                Exposure($"{prefix}-stale", at: DateTimeOffset.UtcNow.AddDays(-30)),
            },
        });
        var body = (await response.Content.ReadFromJsonAsync<IngestResponse>())!;
        List<EventRecord> stored = await WaitForStoredAsync(prefix, 1);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, body.Accepted);
        Assert.Equal([1, 2], body.Rejected.Select(r => r.Index));
        Assert.Single(stored);
        Assert.EndsWith("-good", stored[0].EventId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversionsKeepTheirNameAndValue()
    {
        (_, HttpClient sdk) = await SetupAsync();
        string prefix = Workspace.Unique("conv");

        await sdk.PostAsJsonAsync("/v1/events", new
        {
            events = new[]
            {
                new { eventId = $"{prefix}-1", type = "conversion", name = "purchase", value = 42.5, userId = "u", timestamp = DateTimeOffset.UtcNow },
            },
        });
        List<EventRecord> stored = await WaitForStoredAsync(prefix, 1);

        Assert.Equal(("purchase", 42.5), (stored.Single().Name, stored.Single().Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task EmptyOrOversizedBatchesAreABadRequest(int count)
    {
        (_, HttpClient sdk) = await SetupAsync();
        object[] events = Enumerable.Range(0, count).Select(i => Exposure($"x-{i}")).ToArray();

        HttpResponseMessage response = await sdk.PostAsJsonAsync("/v1/events", new { events });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ABatchOfExactlyTheMaximumIsAccepted()
    {
        (_, HttpClient sdk) = await SetupAsync();
        string prefix = Workspace.Unique("max");
        object[] events = Enumerable.Range(0, 500).Select(i => Exposure($"{prefix}-{i}")).ToArray();

        HttpResponseMessage response = await sdk.PostAsJsonAsync("/v1/events", new { events });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(500, (await WaitForStoredAsync(prefix, 500)).Count);
    }

    // ---- who may send ----

    [Fact]
    public async Task OnlyKeysWithEventsWriteMaySend()
    {
        (_, HttpClient readOnly) = await SetupAsync("config:read");

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/v1/events", new { events = new[] { Exposure("x") } })).StatusCode);
    }

    [Fact]
    public async Task PeopleAndAnonymousCallersCannotSend()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        object body = new { events = new[] { Exposure("x") } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/v1/events", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ws.Client.PostAsJsonAsync("/v1/events", body)).StatusCode);
    }

    [Fact]
    public async Task EventsLandInTheKeysOwnEnvironmentWhateverThePayloadSays()
    {
        (Workspace a, HttpClient sdkA) = await SetupAsync();
        (Workspace b, _) = await SetupAsync();
        string prefix = Workspace.Unique("own");

        // Nothing in the payload can name another project or environment; only the key decides.
        await sdkA.PostAsJsonAsync("/v1/events", new
        {
            events = new[]
            {
                new { eventId = $"{prefix}-1", type = "exposure", experimentKey = "checkout", variationKey = "control", userId = "u",
                      timestamp = DateTimeOffset.UtcNow, projectId = Guid.NewGuid(), environmentId = Guid.NewGuid() },
            },
        });
        EventRecord stored = (await WaitForStoredAsync(prefix, 1)).Single();

        Assert.Equal(a.Project, await ProjectKeyAsync(stored.ProjectId));
        Assert.NotEqual(b.Project, await ProjectKeyAsync(stored.ProjectId));
    }

    // ---- backpressure ----

    [Fact]
    public async Task AFullQueueAnswers429WithRetryAfterAndStoresNothing()
    {
        (_, HttpClient sdkNormal) = await SetupAsync();
        _ = sdkNormal;
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey key = await ws.CreateKeyAsync("events:write");
        using WebApplicationFactoryWithFullQueue full = new(factory);
        HttpClient sdk = full.CreateClientWithKey(key.Secret);
        string prefix = Workspace.Unique("full");

        HttpResponseMessage response = await sdk.PostAsJsonAsync("/v1/events", new { events = new[] { Exposure($"{prefix}-1") } });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter is not null);
        await Task.Delay(200);
        Assert.Empty(await StoredAsync(prefix));
    }

    [Fact]
    public async Task EachKeyIsRateLimitedIndependently()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey first = await ws.CreateKeyAsync("events:write");
        IssuedKey second = await ws.CreateKeyAsync("events:write");
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:EventsPerMinute", "2"));
        HttpClient Client(IssuedKey k) => new HttpClientFactoryShim(limited).Create(k.Secret);
        object body = new { events = new[] { Exposure(Workspace.Unique("rl")) } };

        HttpClient a = Client(first);
        HttpStatusCode[] statuses =
        [
            (await a.PostAsJsonAsync("/v1/events", body)).StatusCode,
            (await a.PostAsJsonAsync("/v1/events", body)).StatusCode,
            (await a.PostAsJsonAsync("/v1/events", body)).StatusCode,
        ];
        HttpResponseMessage other = await Client(second).PostAsJsonAsync("/v1/events", body);

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode); // another key has its own allowance
    }

    private async Task<string> ProjectKeyAsync(Guid projectId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        return await db.Projects.IgnoreQueryFilters().Where(p => p.Id == projectId).Select(p => p.Key).SingleAsync();
    }

    private sealed class AlwaysFullSink : IEventSink
    {
        public bool TryAccept(IReadOnlyList<EventRecord> events) => false;
    }

    /// <summary>The same app and database, but with an ingestion queue that never has room.</summary>
    private sealed class WebApplicationFactoryWithFullQueue : IDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _inner;

        public WebApplicationFactoryWithFullQueue(ApiFactory parent) =>
            _inner = parent.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IEventSink, AlwaysFullSink>()));

        public HttpClient CreateClientWithKey(string secret)
        {
            HttpClient client = _inner.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return client;
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class HttpClientFactoryShim(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> inner)
    {
        public HttpClient Create(string secret)
        {
            HttpClient client = inner.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return client;
        }
    }
}
