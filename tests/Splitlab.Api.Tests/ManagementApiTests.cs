using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Sdk;
using Splitlab.Sdk.Config;

namespace Splitlab.Api.Tests;

public sealed class ManagementApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _http;

    private readonly ApiFactory _factory;

    public ManagementApiTests(ApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    // Keys are unique per test because the factory (and its database) is shared by the class.
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private async Task<(string Project, string Environment)> SeedAsync()
    {
        string project = Unique("proj");
        string environment = Unique("env");
        Assert.Equal(HttpStatusCode.Created, (await _http.PostAsJsonAsync("/v1/projects", new { key = project, name = "P" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await _http.PostAsJsonAsync($"/v1/projects/{project}/environments", new { key = environment, name = "E" })).StatusCode);
        return (project, environment);
    }

    private static object TwoWay => new[]
    {
        new { key = "control", trafficBasisPoints = 5_000 },
        new { key = "treatment", trafficBasisPoints = 5_000 },
    };

    private async Task<ExperimentView> CreateExperimentAsync(string project, string environment, string key = "checkout")
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/v1/projects/{project}/environments/{environment}/experiments",
            new { key, name = "Checkout", variations = TwoWay });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExperimentView>())!;
    }

    private sealed record ExperimentView(string Key, string Status, int Version);

    [Fact]
    public async Task DuplicateProjectIsAConflictProblem()
    {
        string key = Unique("dup");
        await _http.PostAsJsonAsync("/v1/projects", new { key, name = "P" });

        HttpResponseMessage second = await _http.PostAsJsonAsync("/v1/projects", new { key, name = "P" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task InvalidInputIsABadRequest()
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("/v1/projects", new { key = "Bad Key", name = "P" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownResourcesAre404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/v1/projects/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/v1/config/nope")).StatusCode);
    }

    [Fact]
    public async Task ExperimentLifecycleOverHttp()
    {
        (string project, string environment) = await SeedAsync();
        string baseUrl = $"/v1/projects/{project}/environments/{environment}/experiments";
        ExperimentView created = await CreateExperimentAsync(project, environment);

        HttpResponseMessage started = await _http.PostAsJsonAsync($"{baseUrl}/checkout/start", new { version = created.Version });
        var running = (await started.Content.ReadFromJsonAsync<ExperimentView>())!;
        HttpResponseMessage stopped = await _http.PostAsJsonAsync($"{baseUrl}/checkout/stop", new { version = running.Version });
        var final = (await stopped.Content.ReadFromJsonAsync<ExperimentView>())!;

        Assert.Equal(("Draft", 1), (created.Status, created.Version));
        Assert.Equal(("Running", 2), (running.Status, running.Version));
        Assert.Equal(("Stopped", 3), (final.Status, final.Version));
    }

    [Fact]
    public async Task StaleVersionIsAConflict()
    {
        (string project, string environment) = await SeedAsync();
        await CreateExperimentAsync(project, environment);

        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/v1/projects/{project}/environments/{environment}/experiments/checkout/start", new { version = 99 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task VariationsCanBeReplacedInDraftButNotWhileRunning()
    {
        (string project, string environment) = await SeedAsync();
        string baseUrl = $"/v1/projects/{project}/environments/{environment}/experiments/checkout";
        ExperimentView created = await CreateExperimentAsync(project, environment);
        var three = new[]
        {
            new { key = "a", trafficBasisPoints = 3_000 },
            new { key = "b", trafficBasisPoints = 3_000 },
            new { key = "c", trafficBasisPoints = 3_000 },
        };

        HttpResponseMessage replaced = await _http.PutAsJsonAsync($"{baseUrl}/variations", new { version = created.Version, variations = three });
        var afterReplace = (await replaced.Content.ReadFromJsonAsync<ExperimentView>())!;
        await _http.PostAsJsonAsync($"{baseUrl}/start", new { version = afterReplace.Version });
        HttpResponseMessage tooLate = await _http.PutAsJsonAsync($"{baseUrl}/variations", new { version = afterReplace.Version + 1, variations = three });

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(2, afterReplace.Version);
        Assert.Equal(HttpStatusCode.BadRequest, tooLate.StatusCode);
    }

    [Fact]
    public async Task DatafileOnlyContainsRunningExperimentsAndSupportsETag()
    {
        (string project, string environment) = await SeedAsync();
        ExperimentView draft = await CreateExperimentAsync(project, environment, "still-draft");
        ExperimentView live = await CreateExperimentAsync(project, environment, "live");

        HttpResponseMessage empty = await _http.GetAsync($"/v1/config/{environment}");
        await _http.PostAsJsonAsync(
            $"/v1/projects/{project}/environments/{environment}/experiments/live/start", new { version = live.Version });
        HttpResponseMessage published = await _http.GetAsync($"/v1/config/{environment}");
        string etag = published.Headers.ETag!.ToString();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/v1/config/{environment}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage notModified = await _http.SendAsync(conditional);

        Assert.Equal(1, draft.Version);
        Assert.Contains("\"experiments\":[]", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        string body = await published.Content.ReadAsStringAsync();
        Assert.Contains("\"live\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("still-draft", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    [Fact]
    public async Task TheRealSdkConsumesTheDatafileEndpoint()
    {
        (string project, string environment) = await SeedAsync();
        ExperimentView live = await CreateExperimentAsync(project, environment, "live");
        await _http.PostAsJsonAsync(
            $"/v1/projects/{project}/environments/{environment}/experiments/live/start", new { version = live.Version });

        var source = new HttpDatafileSource(_http, new Uri("http://localhost/"), environment);
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
    public async Task EveryChangeIsAudited()
    {
        (string project, string environment) = await SeedAsync();
        await CreateExperimentAsync(project, environment);

        // project.created, environment.created, experiment.created
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Splitlab.Infrastructure.Persistence.SplitlabDbContext>();
        var actions = db.AuditEntries.Where(a => a.Detail == project || a.Detail == environment || a.Detail == "checkout")
            .Select(a => a.Action).ToList();

        Assert.Contains("project.created", actions);
        Assert.Contains("environment.created", actions);
        Assert.Contains("experiment.created", actions);
    }
}
