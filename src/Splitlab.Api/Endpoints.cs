using Splitlab.Application;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Api;

public sealed record CreateProjectRequest(string? Key, string? Name);

public sealed record CreateEnvironmentRequest(string? Key, string? Name);

public sealed record VariationDto(string Key, int TrafficBasisPoints);

public sealed record CreateExperimentRequest(string? Key, string? Name, List<VariationDto>? Variations);

/// <param name="Version">The experiment version the client last saw (optimistic concurrency).</param>
public sealed record ReplaceVariationsRequest(int Version, List<VariationDto>? Variations);

public sealed record VersionedRequest(int Version);

public sealed record ProjectResponse(string Key, string Name)
{
    public static ProjectResponse From(Project p) => new(p.Key, p.Name);
}

public sealed record EnvironmentResponse(string Key, string Name, int ConfigVersion)
{
    public static EnvironmentResponse From(ProjectEnvironment e) => new(e.Key, e.Name, e.ConfigVersion);
}

public sealed record ExperimentResponse(
    string Key, string Name, string Status, int Version, IReadOnlyList<VariationDto> Variations)
{
    public static ExperimentResponse From(Experiment e) => new(
        e.Key,
        e.Name,
        e.Status.ToString(),
        e.Version,
        e.Variations.Select(v => new VariationDto(v.Key, v.TrafficBasisPoints)).ToList());
}

internal static class Endpoints
{
    private static IEnumerable<VariationSpec> ToSpecs(List<VariationDto>? variations) =>
        (variations ?? []).Select(v => new VariationSpec(v.Key, v.TrafficBasisPoints));

    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder projects = app.MapGroup("/v1/projects");

        projects.MapPost("/", async (CreateProjectRequest body, ProjectService svc, CancellationToken ct) =>
        {
            Project p = await svc.CreateProjectAsync(body.Key, body.Name, ct);
            return Results.Created($"/v1/projects/{p.Key}", ProjectResponse.From(p));
        });

        projects.MapGet("/", async (ProjectService svc, CancellationToken ct) =>
            (await svc.ListProjectsAsync(ct)).Select(ProjectResponse.From));

        projects.MapGet("/{project}", async (string project, ProjectService svc, CancellationToken ct) =>
            ProjectResponse.From(await svc.GetProjectAsync(project, ct)));

        projects.MapPost("/{project}/environments", async (
            string project, CreateEnvironmentRequest body, ProjectService svc, CancellationToken ct) =>
        {
            ProjectEnvironment e = await svc.CreateEnvironmentAsync(project, body.Key, body.Name, ct);
            return Results.Created($"/v1/projects/{project}/environments/{e.Key}", EnvironmentResponse.From(e));
        });

        projects.MapGet("/{project}/environments", async (string project, ProjectService svc, CancellationToken ct) =>
            (await svc.ListEnvironmentsAsync(project, ct)).Select(EnvironmentResponse.From));

        RouteGroupBuilder experiments = projects.MapGroup("/{project}/environments/{environment}/experiments");

        experiments.MapPost("/", async (
            string project, string environment, CreateExperimentRequest body, ExperimentService svc, CancellationToken ct) =>
        {
            Experiment e = await svc.CreateAsync(project, environment, body.Key, body.Name, ToSpecs(body.Variations), ct);
            return Results.Created(
                $"/v1/projects/{project}/environments/{environment}/experiments/{e.Key}", ExperimentResponse.From(e));
        });

        experiments.MapGet("/", async (string project, string environment, ExperimentService svc, CancellationToken ct) =>
            (await svc.ListAsync(project, environment, ct)).Select(ExperimentResponse.From));

        experiments.MapGet("/{key}", async (
            string project, string environment, string key, ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(await svc.GetAsync(project, environment, key, ct)));

        experiments.MapPut("/{key}/variations", async (
            string project, string environment, string key, ReplaceVariationsRequest body,
            ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(
                await svc.ReplaceVariationsAsync(project, environment, key, body.Version, ToSpecs(body.Variations), ct)));

        experiments.MapPost("/{key}/start", async (
            string project, string environment, string key, VersionedRequest body, ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(await svc.StartAsync(project, environment, key, body.Version, ct)));

        experiments.MapPost("/{key}/stop", async (
            string project, string environment, string key, VersionedRequest body, ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(await svc.StopAsync(project, environment, key, body.Version, ct)));
    }

    /// <summary>The endpoint the SDK polls (ADR-0007). Supports <c>If-None-Match</c>.</summary>
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/config/{environmentKey}", async (
            string environmentKey, HttpContext http, DatafileService svc, CancellationToken ct) =>
        {
            DatafileResult result = await svc.GetAsync(environmentKey, http.Request.Headers.IfNoneMatch.ToString(), ct);
            http.Response.Headers.ETag = result.ETag;

            return result.NotModified
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Content(result.Json, "application/json");
        });
    }
}
