using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Application;

/// <summary>The requested thing does not exist (HTTP 404).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>The request conflicts with current state, for example a duplicate key or a stale version (HTTP 409).</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }

    public ConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IProjectRepository
{
    Task<Project?> FindProjectAsync(string key, CancellationToken ct);

    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct);

    Task<ProjectEnvironment?> FindEnvironmentAsync(Guid projectId, string key, CancellationToken ct);

    /// <summary>Finds an environment by its globally unique key, as used by the SDK datafile endpoint.</summary>
    Task<ProjectEnvironment?> FindEnvironmentByKeyAsync(string key, CancellationToken ct);

    Task<IReadOnlyList<ProjectEnvironment>> ListEnvironmentsAsync(Guid projectId, CancellationToken ct);

    void Add(Project project);

    void Add(ProjectEnvironment environment);
}

public interface IExperimentRepository
{
    Task<Experiment?> FindAsync(Guid environmentId, string key, CancellationToken ct);

    Task<IReadOnlyList<Experiment>> ListAsync(Guid environmentId, CancellationToken ct);

    Task<IReadOnlyList<Experiment>> ListRunningAsync(Guid environmentId, CancellationToken ct);

    void Add(Experiment experiment);
}

public interface IAuditLog
{
    /// <summary>Stages an audit entry; it is persisted by the same <see cref="IUnitOfWork"/> commit as the change itself.</summary>
    void Record(Guid projectId, string action, string entityType, Guid entityId, string? detail = null);
}

public interface IUnitOfWork
{
    /// <exception cref="ConflictException">A unique key or concurrency check failed.</exception>
    Task CommitAsync(CancellationToken ct);
}
