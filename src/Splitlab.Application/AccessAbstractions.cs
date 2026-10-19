using Splitlab.Domain.Projects;

namespace Splitlab.Application;

/// <summary>The authenticated caller, as far as use cases and the audit log care.</summary>
public interface ICurrentUser
{
    /// <summary>The identity provider's <c>sub</c> for a person; <c>null</c> for anonymous or machine callers.</summary>
    string? Subject { get; }

    /// <summary>A label for the audit log: the subject, an SDK key prefix, or "anonymous".</summary>
    string Actor { get; }
}

/// <summary>
/// The project the current request is allowed to touch. Starts empty, which means no tenant data is visible
/// or writable (fail closed); authorization sets it once the caller's access to a project is established.
/// </summary>
public interface ITenantContext
{
    Guid? ProjectId { get; }

    void Use(Guid projectId);
}

public sealed record ProjectAccess(Guid ProjectId, ProjectRole Role);

/// <summary>Lookups that must work before a tenant is known. Everything else is tenant-scoped by the data layer.</summary>
public interface IAccessRepository
{
    Task<ProjectAccess?> GetAccessAsync(string projectKey, string subject, CancellationToken ct);

    Task<IReadOnlyList<Project>> ListProjectsForAsync(string subject, CancellationToken ct);

    /// <summary>Project keys are globally unique, so this deliberately looks across tenants.</summary>
    Task<bool> ProjectKeyExistsAsync(string key, CancellationToken ct);

    Task<ApiKey?> FindApiKeyByPrefixAsync(string prefix, CancellationToken ct);
}

public interface IMemberRepository
{
    Task<ProjectMember?> FindAsync(string subject, CancellationToken ct);

    Task<IReadOnlyList<ProjectMember>> ListAsync(CancellationToken ct);

    Task<int> CountAdminsAsync(CancellationToken ct);

    void Add(ProjectMember member);

    void Remove(ProjectMember member);
}

public interface IApiKeyRepository
{
    Task<ApiKey?> FindAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ApiKey>> ListAsync(Guid environmentId, CancellationToken ct);

    void Add(ApiKey key);
}
