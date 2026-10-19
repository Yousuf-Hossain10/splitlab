using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Application;
using Splitlab.Application.Events;
using Splitlab.Application.Results;
using Splitlab.Domain.Events;
using Splitlab.Domain.Statistics;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Api.Tests;

/// <summary>
/// The rollups must say exactly what recomputing from raw events would say, however the events arrive.
/// The oracle is <see cref="ResultsAggregator"/>, which works from the raw events alone.
/// </summary>
public sealed class RollupTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly string[] Keys = ["control", "treatment"];

    private sealed record Counts(Dictionary<string, (long Exposed, long Converted)> ByVariation, long Excluded, long Unknown);

    private async Task<(Guid Project, Guid Environment)> IdsAsync(Workspace ws)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        Guid project = await db.Projects.IgnoreQueryFilters().Where(p => p.Key == ws.Project).Select(p => p.Id).SingleAsync();
        Guid env = await db.Environments.IgnoreQueryFilters().Where(e => e.ProjectId == project && e.Key == ws.Environment).Select(e => e.Id).SingleAsync();
        return (project, env);
    }

    /// <summary>A messy but realistic event stream: repeats, users who saw both arms, an undefined arm, conversions before and after exposure.</summary>
    private static List<EventRecord> GenerateEvents(Guid project, Guid env, int seed, int users = 300)
    {
        var random = new Random(seed);
        var events = new List<EventRecord>();

        EventRecord Make(string type, string user, string? variation, string? metric, int minutesAgo)
        {
            Assert.True(
                EventRecord.TryCreate(
                    project, env, Guid.NewGuid().ToString("N"), type, type == "exposure" ? "checkout" : null, variation, metric, null, user,
                    Now.AddMinutes(-minutesAgo), Now, out EventRecord? e, out string? error),
                error);
            return e!;
        }

        for (int i = 0; i < users; i++)
        {
            string user = $"user-{i}";
            string arm = random.Next(100) < 3 ? "ghost" : Keys[random.Next(2)];
            int first = random.Next(1_000, 3_000);

            events.Add(Make("exposure", user, arm, null, first));
            for (int repeat = random.Next(0, 4); repeat > 0; repeat--)
            {
                events.Add(Make("exposure", user, arm, null, first - random.Next(0, 500)));
            }

            if (random.Next(100) < 6)
            {
                events.Add(Make("exposure", user, arm == "control" ? "treatment" : "control", null, random.Next(900, 3_000)));
            }

            if (random.Next(100) < 35)
            {
                // Some land before the first exposure, some exactly at it.
                int offset = random.Next(0, 10) == 0 ? 0 : random.Next(-200, 600);
                events.Add(Make("conversion", user, null, "purchase", first - offset));
            }

            if (random.Next(100) < 20)
            {
                events.Add(Make("conversion", user, null, "signup", first - random.Next(0, 300)));
            }
        }

        return events;
    }

    private static Counts Oracle(IEnumerable<EventRecord> events, string metric)
    {
        var aggregator = new ResultsAggregator(Keys);
        foreach (EventRecord e in events)
        {
            if (e.Kind == EventKind.Exposure)
            {
                aggregator.AddExposure(e.UserId, e.VariationKey!, e.OccurredAt);
            }
            else if (e.Name == metric)
            {
                aggregator.AddConversion(e.UserId, e.OccurredAt);
            }
        }

        AggregatedCounts result = aggregator.Complete();
        return new Counts(result.Counts.ToDictionary(c => c.Key, c => (c.Exposed, c.Converted)), result.ExcludedUsers, result.UnknownVariationUsers);
    }

    private async Task<Counts> FromRollupsAsync(Guid project, Guid env, string metric)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(project);
        ExperimentCounts counts = await scope.ServiceProvider.GetRequiredService<IExperimentCountsReader>()
            .ReadAsync(env, "checkout", metric, default);

        var byVariation = Keys.ToDictionary(k => k, k => counts.ByVariation.Where(c => c.VariationKey == k).Select(c => (c.Exposed, c.Converted)).SingleOrDefault());
        long unknown = counts.ByVariation.Where(c => !Keys.Contains(c.VariationKey)).Sum(c => c.Exposed);
        return new Counts(byVariation, counts.ExcludedUsers, unknown);
    }

    private async Task StoreInRandomBatchesAsync(IReadOnlyList<EventRecord> events, int seed, bool resendSome)
    {
        var random = new Random(seed);
        List<EventRecord> shuffled = events.OrderBy(_ => random.Next()).ToList();
        if (resendSome)
        {
            // SDK retries: an arbitrary subset arrives a second (and third) time, possibly in other batches.
            shuffled.AddRange(events.Where(_ => random.Next(4) == 0));
            shuffled.AddRange(events.Where(_ => random.Next(10) == 0));
            shuffled = shuffled.OrderBy(_ => random.Next()).ToList();
        }

        using IServiceScope scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        for (int i = 0; i < shuffled.Count;)
        {
            int size = random.Next(1, 120);
            await store.InsertAsync(shuffled.Skip(i).Take(size).ToList(), default);
            i += size;
        }
    }

    private static void AssertSame(Counts expected, Counts actual)
    {
        Assert.Equal(expected.Excluded, actual.Excluded);
        Assert.Equal(expected.Unknown, actual.Unknown);
        foreach (string key in Keys)
        {
            Assert.Equal(expected.ByVariation[key], actual.ByVariation[key]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RollupsMatchTheRawEventsWhateverTheBatchingOrderOrRetries(int seed)
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        (Guid project, Guid env) = await IdsAsync(ws);
        List<EventRecord> events = GenerateEvents(project, env, seed);

        await StoreInRandomBatchesAsync(events, seed * 100, resendSome: true);

        Counts expected = Oracle(events, "purchase");
        Assert.True(expected.Excluded > 0 && expected.Unknown > 0, "the generator should exercise exclusions and unknown arms");
        AssertSame(expected, await FromRollupsAsync(project, env, "purchase"));
        AssertSame(Oracle(events, "signup"), await FromRollupsAsync(project, env, "signup"));
    }

    [Fact]
    public async Task StoringTheSameEventsAgainChangesNothing()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        (Guid project, Guid env) = await IdsAsync(ws);
        List<EventRecord> events = GenerateEvents(project, env, 7);
        await StoreInRandomBatchesAsync(events, 1, resendSome: false);
        Counts before = await FromRollupsAsync(project, env, "purchase");

        await StoreInRandomBatchesAsync(events, 2, resendSome: false);

        AssertSame(before, await FromRollupsAsync(project, env, "purchase"));
    }

    [Fact]
    public async Task ALateEventThatChangesTheAnswerIsFoldedIn()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        (Guid project, Guid env) = await IdsAsync(ws);
        List<EventRecord> events = GenerateEvents(project, env, 5, users: 80);
        await StoreInRandomBatchesAsync(events, 1, resendSome: false);

        // A previously clean control user turns out to have also seen the treatment.
        EventRecord target = events.First(e => e.Kind == EventKind.Exposure && e.VariationKey == "control"
            && events.Where(x => x.UserId == e.UserId && x.Kind == EventKind.Exposure).All(x => x.VariationKey == "control"));
        Assert.True(EventRecord.TryCreate(
            project, env, "late", "exposure", "checkout", "treatment", null, null, target.UserId, Now.AddMinutes(-10), Now, out EventRecord? late, out _));
        events.Add(late!);
        await StoreInRandomBatchesAsync([late!], 1, resendSome: false);

        AssertSame(Oracle(events, "purchase"), await FromRollupsAsync(project, env, "purchase"));
    }

    // ---- rebuild ----

    private async Task WipeRollupsAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM experiment_user_states");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM metric_user_states");
    }

    [Fact]
    public async Task RebuildRestoresWipedRollupsFromTheRawEvents()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        (Guid project, Guid env) = await IdsAsync(ws);
        List<EventRecord> events = GenerateEvents(project, env, 11, users: 150);
        await StoreInRandomBatchesAsync(events, 1, resendSome: true);
        Counts expected = Oracle(events, "purchase");

        await WipeRollupsAsync();
        Assert.Equal(0, (await FromRollupsAsync(project, env, "purchase")).ByVariation.Values.Sum(v => v.Exposed));

        HttpResponseMessage response = await ws.Client.PostAsync($"{ws.EnvironmentUrl}/rollups/rebuild", null);
        using JsonDocument summary = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(events.Count, summary.RootElement.GetProperty("events").GetInt64());
        AssertSame(expected, await FromRollupsAsync(project, env, "purchase"));
        AssertSame(Oracle(events, "signup"), await FromRollupsAsync(project, env, "signup"));
    }

    [Fact]
    public async Task RebuildingTwiceGivesTheSameRollups()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        (Guid project, Guid env) = await IdsAsync(ws);
        List<EventRecord> events = GenerateEvents(project, env, 13, users: 100);
        await StoreInRandomBatchesAsync(events, 1, resendSome: false);

        await ws.Client.PostAsync($"{ws.EnvironmentUrl}/rollups/rebuild", null);
        await ws.Client.PostAsync($"{ws.EnvironmentUrl}/rollups/rebuild", null);

        AssertSame(Oracle(events, "purchase"), await FromRollupsAsync(project, env, "purchase"));
    }

    [Fact]
    public async Task RebuildingOneEnvironmentLeavesOthersAlone()
    {
        Workspace a = await Workspace.CreateAsync(factory);
        Workspace b = await Workspace.CreateAsync(factory);
        (Guid projectA, Guid envA) = await IdsAsync(a);
        (Guid projectB, Guid envB) = await IdsAsync(b);
        List<EventRecord> eventsA = GenerateEvents(projectA, envA, 21, users: 60);
        List<EventRecord> eventsB = GenerateEvents(projectB, envB, 22, users: 60);
        await StoreInRandomBatchesAsync(eventsA, 1, resendSome: false);
        await StoreInRandomBatchesAsync(eventsB, 1, resendSome: false);

        await a.Client.PostAsync($"{a.EnvironmentUrl}/rollups/rebuild", null);

        AssertSame(Oracle(eventsA, "purchase"), await FromRollupsAsync(projectA, envA, "purchase"));
        AssertSame(Oracle(eventsB, "purchase"), await FromRollupsAsync(projectB, envB, "purchase"));
    }

    [Fact]
    public async Task OnlyAdminsOfTheProjectMayRebuild()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        HttpClient editor = await ws.AddMemberAsync(Workspace.Unique("editor"), "Editor");
        Workspace outsider = await Workspace.CreateAsync(factory);
        string url = $"{ws.EnvironmentUrl}/rollups/rebuild";

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ws.Client.PostAsync($"/v1/projects/{ws.Project}/environments/nope/rollups/rebuild", null)).StatusCode);
    }

    [Fact]
    public async Task ResultsFromRollupsAgreeWithTheRawEventAnalysis()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        (Guid project, Guid env) = await IdsAsync(ws);
        List<EventRecord> events = GenerateEvents(project, env, 31, users: 600);
        await StoreInRandomBatchesAsync(events, 1, resendSome: true);
        Counts raw = Oracle(events, "purchase");
        ExperimentAnalysis expected = ExperimentAnalyzer.Analyze(
            Keys.Select(k => new VariationData(k, 5_000, raw.ByVariation[k].Exposed, raw.ByVariation[k].Converted)).ToList());

        HttpResponseMessage response = await ws.Client.GetAsync($"{ws.EnvironmentUrl}/experiments/checkout/results?metric=purchase");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement results = body.RootElement.GetProperty("results");
        JsonElement variations = results.GetProperty("analysis").GetProperty("variations");

        Assert.Equal(raw.Excluded, results.GetProperty("excludedUsers").GetInt64());
        Assert.Equal(raw.Unknown, results.GetProperty("unknownVariationUsers").GetInt64());
        Assert.Equal(expected.Variations[1].ProbabilityToBeatControl!.Value, variations[1].GetProperty("probabilityToBeatControl").GetDouble(), 12);
        Assert.Equal(expected.Variations[0].PosteriorMean, variations[0].GetProperty("posteriorMean").GetDouble(), 12);
    }
}
