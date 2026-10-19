using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Infrastructure.Persistence;
using Splitlab.Sdk;
using Splitlab.Sdk.Config;

namespace Splitlab.Api.Tests;

public sealed class ManagementApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DuplicateProjectIsAConflictProblem()
    {
        Workspace ws = await Workspace.CreateAsync(factory);

        HttpResponseMessage second = await factory.ClientFor("someone-else")
            .PostAsJsonAsync("/v1/projects", new { key = ws.Project, name = "P" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task InvalidInputIsABadRequest()
    {
        HttpResponseMessage response = await factory.ClientFor(Workspace.Unique("u"))
            .PostAsJsonAsync("/v1/projects", new { key = "Bad Key", name = "P" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatorBecomesAdminAndSeesOnlyTheirOwnProjects()
    {
        Workspace mine = await Workspace.CreateAsync(factory);
        Workspace theirs = await Workspace.CreateAsync(factory);

        string listed = await mine.Client.GetStringAsync("/v1/projects");

        Assert.Contains(mine.Project, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(theirs.Project, listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExperimentLifecycleOverHttp()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        ExperimentView created = await ws.CreateExperimentAsync();

        HttpResponseMessage started = await ws.Client.PostAsJsonAsync(
            $"{ws.EnvironmentUrl}/experiments/checkout/start", new { version = created.Version });
        var running = (await started.Content.ReadFromJsonAsync<ExperimentView>())!;
        HttpResponseMessage stopped = await ws.Client.PostAsJsonAsync(
            $"{ws.EnvironmentUrl}/experiments/checkout/stop", new { version = running.Version });
        var final = (await stopped.Content.ReadFromJsonAsync<ExperimentView>())!;

        Assert.Equal(("Draft", 1), (created.Status, created.Version));
        Assert.Equal(("Running", 2), (running.Status, running.Version));
        Assert.Equal(("Stopped", 3), (final.Status, final.Version));
    }

    [Fact]
    public async Task StaleVersionIsAConflict()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();

        HttpResponseMessage response = await ws.Client.PostAsJsonAsync(
            $"{ws.EnvironmentUrl}/experiments/checkout/start", new { version = 99 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task VariationsCanBeReplacedInDraftButNotWhileRunning()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        string url = $"{ws.EnvironmentUrl}/experiments/checkout";
        ExperimentView created = await ws.CreateExperimentAsync();
        var three = new[]
        {
            new { key = "a", trafficBasisPoints = 3_000 },
            new { key = "b", trafficBasisPoints = 3_000 },
            new { key = "c", trafficBasisPoints = 3_000 },
        };

        HttpResponseMessage replaced = await ws.Client.PutAsJsonAsync($"{url}/variations", new { version = created.Version, variations = three });
        var afterReplace = (await replaced.Content.ReadFromJsonAsync<ExperimentView>())!;
        await ws.Client.PostAsJsonAsync($"{url}/start", new { version = afterReplace.Version });
        HttpResponseMessage tooLate = await ws.Client.PutAsJsonAsync($"{url}/variations", new { version = afterReplace.Version + 1, variations = three });

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(2, afterReplace.Version);
        Assert.Equal(HttpStatusCode.BadRequest, tooLate.StatusCode);
    }

    [Fact]
    public async Task DatafileOnlyContainsRunningExperimentsAndSupportsETag()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync("still-draft");
        ExperimentView live = await ws.CreateExperimentAsync("live");
        IssuedKey key = await ws.CreateKeyAsync();
        HttpClient sdk = factory.ClientWithKey(key.Secret);
        string url = $"/v1/config/{ws.Environment}";

        HttpResponseMessage empty = await sdk.GetAsync(url);
        await ws.Client.PostAsJsonAsync($"{ws.EnvironmentUrl}/experiments/live/start", new { version = live.Version });
        HttpResponseMessage published = await sdk.GetAsync(url);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", published.Headers.ETag!.ToString());
        HttpResponseMessage notModified = await sdk.SendAsync(conditional);

        Assert.Contains("\"experiments\":[]", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        string body = await published.Content.ReadAsStringAsync();
        Assert.Contains("\"live\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("still-draft", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    [Fact]
    public async Task TheRealSdkConsumesTheDatafileEndpointWithItsKey()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        ExperimentView live = await ws.CreateExperimentAsync("live");
        await ws.Client.PostAsJsonAsync($"{ws.EnvironmentUrl}/experiments/live/start", new { version = live.Version });
        IssuedKey key = await ws.CreateKeyAsync();

        var source = new HttpDatafileSource(
            factory.CreateClient(), new Uri("http://localhost/"), ws.Environment, key.Secret);
        using var client = new SplitlabClient(source);
        bool first = await client.RefreshAsync();
        bool second = await client.RefreshAsync();
        Decision decision = client.Decide("live", "user-42", "default");

        Assert.True(first);
        Assert.False(second); // 304 through the real endpoint
        Assert.True(decision.IsAssigned);
        Assert.True(decision.VariationKey is "control" or "treatment");
    }

    [Fact]
    public async Task ChangesAreAuditedWithTheActor()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();

        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        var actions = db.AuditEntries.IgnoreQueryFilters()
            .Where(a => a.Actor == ws.User)
            .Select(a => a.Action)
            .ToList();

        Assert.Contains("project.created", actions);
        Assert.Contains("environment.created", actions);
        Assert.Contains("experiment.created", actions);
    }
}
