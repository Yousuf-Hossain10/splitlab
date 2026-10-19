using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Application.Events;
using Splitlab.Domain.Events;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Api.Tests;

public sealed class ResultsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<(Guid Project, Guid Environment)> IdsAsync(Workspace ws)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        Guid project = await db.Projects.IgnoreQueryFilters().Where(p => p.Key == ws.Project).Select(p => p.Id).SingleAsync();
        Guid env = await db.Environments.IgnoreQueryFilters().Where(e => e.ProjectId == project && e.Key == ws.Environment).Select(e => e.Id).SingleAsync();
        return (project, env);
    }

    /// <summary>Stores events as the ingestion pipeline would have: <paramref name="users"/> users shown a variation, the first <paramref name="converting"/> of them converting.</summary>
    private async Task SeedArmAsync(Workspace ws, string variation, int users, int converting, string experiment = "checkout", string prefix = "u")
    {
        (Guid project, Guid env) = await IdsAsync(ws);
        var events = new List<EventRecord>();
        for (int i = 0; i < users; i++)
        {
            events.Add(Make(project, env, $"x-{variation}-{prefix}{i}", "exposure", $"{prefix}-{variation}-{i}", experiment, variation, null, Now.AddHours(-2)));
            if (i < converting)
            {
                events.Add(Make(project, env, $"c-{variation}-{prefix}{i}", "conversion", $"{prefix}-{variation}-{i}", null, null, "purchase", Now.AddHours(-1)));
            }
        }

        await StoreAsync(events);
    }

    private static EventRecord Make(Guid project, Guid env, string eventId, string type, string user, string? experiment, string? variation, string? name, DateTimeOffset at)
    {
        Assert.True(EventRecord.TryCreate(project, env, $"{eventId}-{Guid.NewGuid():N}", type, experiment, variation, name, null, user, at, Now, out EventRecord? e, out string? error), error);
        return e!;
    }

    private async Task StoreAsync(List<EventRecord> events)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        foreach (EventRecord[] chunk in events.Chunk(400))
        {
            await scope.ServiceProvider.GetRequiredService<IEventStore>().InsertAsync(chunk, default);
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Url(Workspace ws, string experiment = "checkout", string metric = "purchase") =>
        $"{ws.EnvironmentUrl}/experiments/{experiment}/results?metric={metric}";

    [Fact]
    public async Task ResultsReflectTheStoredEvents()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        await SeedArmAsync(ws, "control", 300, 30);
        await SeedArmAsync(ws, "treatment", 300, 60);

        JsonElement body = await ReadAsync(await ws.Client.GetAsync(Url(ws)));
        JsonElement results = body.GetProperty("results");
        JsonElement variations = results.GetProperty("analysis").GetProperty("variations");

        Assert.Equal("checkout", results.GetProperty("experiment").GetString());
        Assert.Equal("purchase", results.GetProperty("metric").GetString());
        Assert.Equal(300, variations[0].GetProperty("exposed").GetInt64());
        Assert.Equal(30, variations[0].GetProperty("converted").GetInt64());
        Assert.Equal(0.1, variations[0].GetProperty("observedRate").GetDouble(), 9);
        Assert.True(variations[0].GetProperty("isControl").GetBoolean());
        Assert.Equal(JsonValueKind.Null, variations[0].GetProperty("probabilityToBeatControl").ValueKind);
        Assert.Equal(60, variations[1].GetProperty("converted").GetInt64());
        Assert.True(variations[1].GetProperty("probabilityToBeatControl").GetDouble() > 0.99);
        Assert.True(variations[1].GetProperty("credibleLow").GetDouble() < variations[1].GetProperty("posteriorMean").GetDouble());
        Assert.False(results.GetProperty("analysis").GetProperty("sampleRatio").GetProperty("mismatch").GetBoolean());
        Assert.Contains("sample sizes", body.GetProperty("notice").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASkewedSplitIsFlaggedAndNoWinnerIsShown()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync(); // configured 50 / 50
        await SeedArmAsync(ws, "control", 700, 70);
        await SeedArmAsync(ws, "treatment", 300, 90);

        JsonElement analysis = (await ReadAsync(await ws.Client.GetAsync(Url(ws)))).GetProperty("results").GetProperty("analysis");

        Assert.True(analysis.GetProperty("sampleRatio").GetProperty("mismatch").GetBoolean());
        Assert.Equal("Untrustworthy", analysis.GetProperty("recommendation").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, analysis.GetProperty("recommendation").GetProperty("variation").ValueKind);
    }

    [Fact]
    public async Task SmallSamplesKeepCollecting()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        await SeedArmAsync(ws, "control", 20, 5);
        await SeedArmAsync(ws, "treatment", 20, 15);

        JsonElement recommendation = (await ReadAsync(await ws.Client.GetAsync(Url(ws))))
            .GetProperty("results").GetProperty("analysis").GetProperty("recommendation");

        Assert.Equal("KeepCollecting", recommendation.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task UsersInBothVariationsAreExcludedAndReported()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        await SeedArmAsync(ws, "control", 150, 0);
        await SeedArmAsync(ws, "treatment", 150, 0);
        (Guid project, Guid env) = await IdsAsync(ws);
        await StoreAsync(
        [
            Make(project, env, "dup1", "exposure", "u-control-0", "checkout", "treatment", null, Now.AddMinutes(-30)),
            Make(project, env, "dup2", "exposure", "u-control-1", "checkout", "treatment", null, Now.AddMinutes(-30)),
        ]);

        JsonElement results = (await ReadAsync(await ws.Client.GetAsync(Url(ws)))).GetProperty("results");
        JsonElement variations = results.GetProperty("analysis").GetProperty("variations");

        Assert.Equal(2, results.GetProperty("excludedUsers").GetInt64());
        Assert.Equal(148, variations[0].GetProperty("exposed").GetInt64());
        Assert.Equal(150, variations[1].GetProperty("exposed").GetInt64());
    }

    [Fact]
    public async Task OtherMetricsAndOtherExperimentsDoNotLeakIn()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        await SeedArmAsync(ws, "control", 120, 12);
        await SeedArmAsync(ws, "treatment", 120, 12);
        await SeedArmAsync(ws, "control", 500, 500, experiment: "some-other-experiment", prefix: "o");

        JsonElement variations = (await ReadAsync(await ws.Client.GetAsync(Url(ws, metric: "signup"))))
            .GetProperty("results").GetProperty("analysis").GetProperty("variations");

        Assert.Equal(120, variations[0].GetProperty("exposed").GetInt64());
        Assert.Equal(0, variations[0].GetProperty("converted").GetInt64()); // nobody did "signup"
    }

    [Fact]
    public async Task AnotherProjectsEventsAreNeverCounted()
    {
        Workspace a = await Workspace.CreateAsync(factory);
        Workspace b = await Workspace.CreateAsync(factory);
        await a.CreateExperimentAsync();
        await b.CreateExperimentAsync();
        await SeedArmAsync(b, "control", 400, 40);
        await SeedArmAsync(b, "treatment", 400, 80);

        JsonElement variations = (await ReadAsync(await a.Client.GetAsync(Url(a))))
            .GetProperty("results").GetProperty("analysis").GetProperty("variations");

        Assert.Equal(0, variations[0].GetProperty("exposed").GetInt64());
        Assert.Equal(0, variations[1].GetProperty("exposed").GetInt64());
    }

    [Fact]
    public async Task ViewersCanReadResultsButOutsidersCannot()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        HttpClient viewer = await ws.AddMemberAsync(Workspace.Unique("viewer"), "Viewer");
        Workspace outsider = await Workspace.CreateAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Url(ws))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.GetAsync(Url(ws))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url(ws))).StatusCode);
    }

    [Fact]
    public async Task BadRequestsAndMissingThingsGetProperErrors()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await ws.Client.GetAsync($"{ws.EnvironmentUrl}/experiments/checkout/results")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ws.Client.GetAsync(Url(ws, metric: "Not A Key"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ws.Client.GetAsync(Url(ws, experiment: "missing"))).StatusCode);
    }

    [Fact]
    public async Task AnExperimentWithOneVariationCannotBeAnalysed()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.Client.PostAsJsonAsync(
            $"{ws.EnvironmentUrl}/experiments",
            new { key = "solo", name = "Solo", variations = new[] { new { key = "only", trafficBasisPoints = 10_000 } } });

        Assert.Equal(HttpStatusCode.BadRequest, (await ws.Client.GetAsync(Url(ws, experiment: "solo"))).StatusCode);
    }
}
