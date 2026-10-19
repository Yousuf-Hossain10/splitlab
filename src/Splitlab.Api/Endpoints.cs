using Microsoft.AspNetCore.Mvc;
using Splitlab.Api.Auth;
using Splitlab.Application;
using Splitlab.Application.Events;
using Splitlab.Application.Results;
using Splitlab.Domain;
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

public sealed record SetRoleRequest(string? Role);

/// <param name="Permissions">Any of <c>config:read</c>, <c>events:write</c>.</param>
public sealed record CreateApiKeyRequest(string? Name, List<string>? Permissions);

public sealed record ProjectResponse(string Key, string Name)
{
    public static ProjectResponse From(Project p) => new(p.Key, p.Name);
}

public sealed record EnvironmentResponse(string Key, string Name, int ConfigVersion)
{
    public static EnvironmentResponse From(ProjectEnvironment e) => new(e.Key, e.Name, e.ConfigVersion);
}

public sealed record MemberResponse(string Subject, string Role)
{
    public static MemberResponse From(ProjectMember m) => new(m.Subject, m.Role.ToString());
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

/// <summary>An SDK key as listed: the secret is never included.</summary>
public sealed record ApiKeyResponse(Guid Id, string Name, string Prefix, IReadOnlyList<string> Permissions, bool Active)
{
    public static ApiKeyResponse From(ApiKey k) => new(k.Id, k.Name, k.Prefix, PermissionNames(k.Permissions), k.IsActive);

    internal static List<string> PermissionNames(ApiKeyPermissions p)
    {
        var names = new List<string>();
        if (p.HasFlag(ApiKeyPermissions.ConfigRead))
        {
            names.Add(SdkClaims.ConfigRead);
        }

        if (p.HasFlag(ApiKeyPermissions.EventsWrite))
        {
            names.Add(SdkClaims.EventsWrite);
        }

        return names;
    }
}

/// <summary>Returned once, at creation or rotation. <paramref name="Secret"/> cannot be retrieved again.</summary>
public sealed record IssuedApiKeyResponse(Guid Id, string Name, string Prefix, IReadOnlyList<string> Permissions, string Secret)
{
    public static IssuedApiKeyResponse From(IssuedApiKey i) =>
        new(i.Key.Id, i.Key.Name, i.Key.Prefix, ApiKeyResponse.PermissionNames(i.Key.Permissions), i.Secret);
}

internal static class Endpoints
{
    /// <summary>Documents error statuses (as RFC 7807 problem details) for every endpoint in a group.</summary>
    private static TBuilder ProblemResponses<TBuilder>(this TBuilder builder, params int[] statusCodes)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (int code in statusCodes)
        {
            builder.WithMetadata(new ProducesResponseTypeAttribute(typeof(ProblemDetails), code, "application/problem+json"));
        }

        return builder;
    }

    private static IEnumerable<VariationSpec> ToSpecs(List<VariationDto>? variations) =>
        (variations ?? []).Select(v => new VariationSpec(v.Key, v.TrafficBasisPoints));

    private static ApiKeyPermissions ParsePermissions(List<string>? names)
    {
        var result = ApiKeyPermissions.None;
        foreach (string name in names ?? [])
        {
            result |= name switch
            {
                SdkClaims.ConfigRead => ApiKeyPermissions.ConfigRead,
                SdkClaims.EventsWrite => ApiKeyPermissions.EventsWrite,
                _ => throw new DomainException($"Unknown permission '{name}'. Use 'config:read' or 'events:write'."),
            };
        }

        return result;
    }

    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder root = app.MapGroup("/v1/projects").ProblemResponses(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        // Any signed-in person may create a project (and becomes its Admin) or list their own.
        root.MapPost("/", async (CreateProjectRequest body, ProjectService svc, CancellationToken ct) =>
        {
            Project p = await svc.CreateProjectAsync(body.Key, body.Name, ct);
            return Results.Created($"/v1/projects/{p.Key}", ProjectResponse.From(p));
        }).RequireAuthorization(Policies.SignedIn).WithName("createProject").Produces<ProjectResponse>(StatusCodes.Status201Created);

        root.MapGet("/", async (ProjectService svc, CancellationToken ct) =>
            (await svc.ListProjectsAsync(ct)).Select(ProjectResponse.From)).RequireAuthorization(Policies.SignedIn).WithName("listProjects");

        // Everything below is scoped to one project by the role policies, which read {project} from the route.
        RouteGroupBuilder project = root.MapGroup("/{project}");

        project.MapGet("/", async (string project, ProjectService svc, CancellationToken ct) =>
            ProjectResponse.From(await svc.GetProjectAsync(project, ct))).RequireAuthorization(Policies.Viewer).WithName("getProject");

        project.MapGet("/environments", async (string project, ProjectService svc, CancellationToken ct) =>
            (await svc.ListEnvironmentsAsync(project, ct)).Select(EnvironmentResponse.From)).RequireAuthorization(Policies.Viewer).WithName("listEnvironments");

        project.MapPost("/environments", async (
            string project, CreateEnvironmentRequest body, ProjectService svc, CancellationToken ct) =>
        {
            ProjectEnvironment e = await svc.CreateEnvironmentAsync(project, body.Key, body.Name, ct);
            return Results.Created($"/v1/projects/{project}/environments/{e.Key}", EnvironmentResponse.From(e));
        }).RequireAuthorization(Policies.Admin).WithName("createEnvironment").Produces<EnvironmentResponse>(StatusCodes.Status201Created);

        MapMembers(project);
        MapExperiments(project);
        MapApiKeys(project);
        MapRollups(project);
    }

    private static void MapMembers(RouteGroupBuilder project)
    {
        project.MapGet("/members", async (MemberService svc, CancellationToken ct) =>
            (await svc.ListAsync(ct)).Select(MemberResponse.From)).RequireAuthorization(Policies.Admin).WithName("listMembers");

        project.MapPut("/members/{subject}", async (
            string project, string subject, SetRoleRequest body, MemberService svc, CancellationToken ct) =>
        {
            if (!Enum.TryParse(body.Role, ignoreCase: true, out ProjectRole role) || !Enum.IsDefined(role))
            {
                throw new DomainException("Role must be Viewer, Editor or Admin.");
            }

            return MemberResponse.From(await svc.SetRoleAsync(project, subject, role, ct));
        }).RequireAuthorization(Policies.Admin).WithName("setMemberRole");

        project.MapDelete("/members/{subject}", async (
            string project, string subject, MemberService svc, CancellationToken ct) =>
        {
            await svc.RemoveAsync(project, subject, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin).WithName("removeMember").Produces(StatusCodes.Status204NoContent);
    }

    private static void MapExperiments(RouteGroupBuilder project)
    {
        RouteGroupBuilder experiments = project.MapGroup("/environments/{environment}/experiments");

        experiments.MapGet("/", async (string project, string environment, ExperimentService svc, CancellationToken ct) =>
            (await svc.ListAsync(project, environment, ct)).Select(ExperimentResponse.From))
            .RequireAuthorization(Policies.Viewer).WithName("listExperiments");

        experiments.MapGet("/{key}", async (
            string project, string environment, string key, ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(await svc.GetAsync(project, environment, key, ct)))
            .RequireAuthorization(Policies.Viewer).WithName("getExperiment");

        experiments.MapPost("/", async (
            string project, string environment, CreateExperimentRequest body, ExperimentService svc, CancellationToken ct) =>
        {
            Experiment e = await svc.CreateAsync(project, environment, body.Key, body.Name, ToSpecs(body.Variations), ct);
            return Results.Created(
                $"/v1/projects/{project}/environments/{environment}/experiments/{e.Key}", ExperimentResponse.From(e));
        }).RequireAuthorization(Policies.Editor).WithName("createExperiment").Produces<ExperimentResponse>(StatusCodes.Status201Created);

        experiments.MapPut("/{key}/variations", async (
            string project, string environment, string key, ReplaceVariationsRequest body,
            ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(
                await svc.ReplaceVariationsAsync(project, environment, key, body.Version, ToSpecs(body.Variations), ct)))
            .RequireAuthorization(Policies.Editor).WithName("replaceVariations");

        experiments.MapGet("/{key}/results", async (
            string project, string environment, string key, string? metric, ResultsService svc, CancellationToken ct) =>
            Results.Json(new ResultsResponse(await svc.GetAsync(project, environment, key, metric, ct), ResultsResponse.StandardNotice)))
            .RequireAuthorization(Policies.Viewer).WithName("getExperimentResults").Produces<ResultsResponse>();

        experiments.MapPost("/{key}/start", async (
            string project, string environment, string key, VersionedRequest body, ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(await svc.StartAsync(project, environment, key, body.Version, ct)))
            .RequireAuthorization(Policies.Editor).WithName("startExperiment");

        experiments.MapPost("/{key}/stop", async (
            string project, string environment, string key, VersionedRequest body, ExperimentService svc, CancellationToken ct) =>
            ExperimentResponse.From(await svc.StopAsync(project, environment, key, body.Version, ct)))
            .RequireAuthorization(Policies.Editor).WithName("stopExperiment");
    }

    private static void MapRollups(RouteGroupBuilder project)
    {
        // Rollups are maintained automatically as events are stored; this backfills or repairs them from the raw events.
        project.MapPost("/environments/{environment}/rollups/rebuild", async (
            string project, string environment, RollupService svc, CancellationToken ct) =>
            Results.Ok(await svc.RebuildAsync(project, environment, ct)))
            .RequireAuthorization(Policies.Admin).WithName("rebuildRollups").Produces<RebuildSummary>();
    }

    private static void MapApiKeys(RouteGroupBuilder project)
    {
        RouteGroupBuilder keys = project.MapGroup("/environments/{environment}/api-keys").RequireAuthorization(Policies.Admin);

        keys.MapGet("/", async (string project, string environment, ApiKeyService svc, CancellationToken ct) =>
            (await svc.ListAsync(project, environment, ct)).Select(ApiKeyResponse.From)).WithName("listApiKeys");

        keys.MapPost("/", async (
            string project, string environment, CreateApiKeyRequest body, ApiKeyService svc, CancellationToken ct) =>
        {
            IssuedApiKey issued = await svc.CreateAsync(project, environment, body.Name, ParsePermissions(body.Permissions), ct);
            return Results.Created(
                $"/v1/projects/{project}/environments/{environment}/api-keys/{issued.Key.Id}", IssuedApiKeyResponse.From(issued));
        }).WithName("createApiKey").Produces<IssuedApiKeyResponse>(StatusCodes.Status201Created);

        keys.MapPost("/{id:guid}/revoke", async (
            string project, string environment, Guid id, ApiKeyService svc, CancellationToken ct) =>
        {
            await svc.RevokeAsync(project, environment, id, ct);
            return Results.NoContent();
        }).WithName("revokeApiKey").Produces(StatusCodes.Status204NoContent);

        keys.MapPost("/{id:guid}/rotate", async (
            string project, string environment, Guid id, ApiKeyService svc, CancellationToken ct) =>
            IssuedApiKeyResponse.From(await svc.RotateAsync(project, environment, id, ct))).WithName("rotateApiKey");
    }

    /// <summary>The endpoint the SDK polls (ADR-0007). Needs an SDK key with <c>config:read</c> for that environment.</summary>
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/config/{environmentKey}", async (
            string environmentKey, HttpContext http, DatafileService svc, CancellationToken ct) =>
        {
            var keyEnvironment = Guid.Parse(http.User.FindFirst(SdkClaims.Environment)!.Value);
            DatafileResult result = await svc.GetAsync(
                environmentKey, keyEnvironment, http.Request.Headers.IfNoneMatch.ToString(), ct);
            http.Response.Headers.ETag = result.ETag;

            return result.NotModified
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Content(result.Json, "application/json");
        }).RequireAuthorization(Policies.ConfigRead).WithName("getDatafile")
            .Produces(StatusCodes.Status200OK, contentType: "application/json").Produces(StatusCodes.Status304NotModified);
    }

    /// <summary>
    /// <c>POST /v1/events</c> (ADR-0005): accepts up to 500 events, queues the valid ones and answers 202.
    /// 429 with <c>Retry-After</c> means the queue is full and the SDK should back off. Needs an SDK key with <c>events:write</c>.
    /// </summary>
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/events", (
            IngestRequest body, HttpContext http, ITenantContext tenant, EventIngestionService svc) =>
        {
            var environmentId = Guid.Parse(http.User.FindFirst(SdkClaims.Environment)!.Value);
            IReadOnlyList<RawEvent>? events = body.Events?
                .Select(e => new RawEvent(e.EventId, e.Type, e.ExperimentKey, e.VariationKey, e.Name, e.Value, e.UserId, e.Timestamp))
                .ToList();

            IngestResult result = svc.Ingest(tenant.ProjectId!.Value, environmentId, events);

            if (result.QueueFull)
            {
                http.Response.Headers.RetryAfter = "1";
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many requests",
                    detail: "The ingestion queue is full. Retry with backoff.");
            }

            return Results.Json(new IngestResultResponse(result.Accepted, result.Rejected), statusCode: StatusCodes.Status202Accepted);
        }).RequireAuthorization(Policies.EventsWrite).RequireRateLimiting(RateLimitPolicies.SdkEvents).WithName("ingestEvents")
            .Produces<IngestResultResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Development convenience: mints a token for the local issuer so the API can be tried without an identity provider.
    /// Only mapped in Development and only when no external authority is configured.
    /// </summary>
    public static void MapDevTokenEndpoint(this IEndpointRouteBuilder app, string signingKey)
    {
        app.MapPost("/dev/token", (DevTokenRequest body) =>
            string.IsNullOrWhiteSpace(body.Subject)
                ? Results.BadRequest("subject is required")
                : Results.Ok(new DevTokenResponse(AuthSetup.MintLocalToken(signingKey, body.Subject, TimeSpan.FromHours(8)))))
            .AllowAnonymous().WithName("createDevToken").Produces<DevTokenResponse>().ProducesProblem(StatusCodes.Status400BadRequest);
    }
}

public sealed record DevTokenRequest(string? Subject);

public sealed record DevTokenResponse(string Token);

public sealed record IngestResultResponse(int Accepted, IReadOnlyList<Rejection> Rejected);

/// <summary>An experiment's results plus the standing caveats that should accompany any reading of them (ADR-0006).</summary>
public sealed record ResultsResponse(ExperimentResults Results, string Notice)
{
    public const string StandardNotice =
        "Probabilities are conditional on the sample sizes shown. Bayesian results tolerate repeated looks better than a "
        + "fixed-horizon test but are not immune to stopping-rule effects, so avoid ending an experiment the moment a number looks good.";
}

/// <param name="Timestamp">When the event happened. Required: a retry must resend the same timestamp so it is recognised as a duplicate.</param>
public sealed record IngestEventDto(
    string? EventId,
    string? Type,
    string? ExperimentKey,
    string? VariationKey,
    string? Name,
    double? Value,
    string? UserId,
    DateTimeOffset? Timestamp);

public sealed record IngestRequest(List<IngestEventDto>? Events);
