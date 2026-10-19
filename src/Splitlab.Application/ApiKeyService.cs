using Splitlab.Domain.Projects;

namespace Splitlab.Application;

/// <summary>An SDK key plus, only at creation or rotation, its one-time secret.</summary>
public sealed record IssuedApiKey(ApiKey Key, string Secret);

public sealed class ApiKeyService(
    IProjectRepository projects,
    IApiKeyRepository keys,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<IssuedApiKey> CreateAsync(
        string projectKey, string environmentKey, string? name, ApiKeyPermissions permissions, CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        return await IssueAsync(project, environment, name, permissions, "apikey.created", ct);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(string projectKey, string environmentKey, CancellationToken ct)
    {
        (_, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        return await keys.ListAsync(environment.Id, ct);
    }

    public async Task RevokeAsync(string projectKey, string environmentKey, Guid id, CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        ApiKey key = await FindAsync(environment, id, ct);

        key.Revoke(clock.GetUtcNow());
        audit.Record(project.Id, "apikey.revoked", nameof(ApiKey), key.Id, key.Prefix);
        await unitOfWork.CommitAsync(ct);
    }

    /// <summary>Issues a replacement with the same name and permissions and revokes the old key in one commit.</summary>
    public async Task<IssuedApiKey> RotateAsync(string projectKey, string environmentKey, Guid id, CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        ApiKey old = await FindAsync(environment, id, ct);
        if (!old.IsActive)
        {
            throw new ConflictException("A revoked key cannot be rotated.");
        }

        old.Revoke(clock.GetUtcNow());
        audit.Record(project.Id, "apikey.revoked", nameof(ApiKey), old.Id, old.Prefix);
        return await IssueAsync(project, environment, old.Name, old.Permissions, "apikey.rotated", ct);
    }

    private async Task<IssuedApiKey> IssueAsync(
        Project project, ProjectEnvironment environment, string? name, ApiKeyPermissions permissions, string action, CancellationToken ct)
    {
        (ApiKey key, string secret) = ApiKey.Issue(project.Id, environment.Id, name, permissions, clock.GetUtcNow());
        keys.Add(key);
        audit.Record(project.Id, action, nameof(ApiKey), key.Id, key.Prefix);
        await unitOfWork.CommitAsync(ct);
        return new IssuedApiKey(key, secret);
    }

    private async Task<(Project, ProjectEnvironment)> ResolveAsync(string projectKey, string environmentKey, CancellationToken ct)
    {
        Project project = await projects.FindProjectAsync(projectKey, ct)
            ?? throw new NotFoundException($"Project '{projectKey}' was not found.");
        ProjectEnvironment environment = await projects.FindEnvironmentAsync(project.Id, environmentKey, ct)
            ?? throw new NotFoundException($"Environment '{environmentKey}' was not found.");
        return (project, environment);
    }

    private async Task<ApiKey> FindAsync(ProjectEnvironment environment, Guid id, CancellationToken ct)
    {
        ApiKey? key = await keys.FindAsync(id, ct);
        return key is not null && key.EnvironmentId == environment.Id
            ? key
            : throw new NotFoundException("API key was not found.");
    }
}
