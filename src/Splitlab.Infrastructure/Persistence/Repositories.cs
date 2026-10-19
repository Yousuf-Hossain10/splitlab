using Microsoft.EntityFrameworkCore;
using Splitlab.Application;
using Splitlab.Domain.Audit;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Infrastructure.Persistence;

internal sealed class ProjectRepository(SplitlabDbContext db) : IProjectRepository
{
    public Task<Project?> FindProjectAsync(string key, CancellationToken ct) =>
        db.Projects.FirstOrDefaultAsync(p => p.Key == key, ct);

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) =>
        await db.Projects.AsNoTracking().OrderBy(p => p.Key).ToListAsync(ct);

    public Task<ProjectEnvironment?> FindEnvironmentAsync(Guid projectId, string key, CancellationToken ct) =>
        db.Environments.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Key == key, ct);

    public Task<ProjectEnvironment?> FindEnvironmentByKeyAsync(string key, CancellationToken ct) =>
        db.Environments.FirstOrDefaultAsync(e => e.Key == key, ct);

    public async Task<IReadOnlyList<ProjectEnvironment>> ListEnvironmentsAsync(Guid projectId, CancellationToken ct) =>
        await db.Environments.AsNoTracking().Where(e => e.ProjectId == projectId).OrderBy(e => e.Key).ToListAsync(ct);

    public void Add(Project project) => db.Projects.Add(project);

    public void Add(ProjectEnvironment environment) => db.Environments.Add(environment);
}

internal sealed class ExperimentRepository(SplitlabDbContext db) : IExperimentRepository
{
    public Task<Experiment?> FindAsync(Guid environmentId, string key, CancellationToken ct) =>
        db.Experiments.FirstOrDefaultAsync(e => e.EnvironmentId == environmentId && e.Key == key, ct);

    public async Task<IReadOnlyList<Experiment>> ListAsync(Guid environmentId, CancellationToken ct) =>
        await db.Experiments.AsNoTracking().Where(e => e.EnvironmentId == environmentId).OrderBy(e => e.Key).ToListAsync(ct);

    public async Task<IReadOnlyList<Experiment>> ListRunningAsync(Guid environmentId, CancellationToken ct) =>
        await db.Experiments.AsNoTracking()
            .Where(e => e.EnvironmentId == environmentId && e.Status == ExperimentStatus.Running)
            .ToListAsync(ct);

    public void Add(Experiment experiment) => db.Experiments.Add(experiment);
}

internal sealed class AuditLog(SplitlabDbContext db, TimeProvider clock, IActorAccessor actor) : IAuditLog
{
    public void Record(Guid projectId, string action, string entityType, Guid entityId, string? detail = null) =>
        db.AuditEntries.Add(AuditEntry.Create(projectId, actor.Actor, action, entityType, entityId, detail, clock.GetUtcNow()));
}

internal sealed class UnitOfWork(SplitlabDbContext db) : IUnitOfWork
{
    public async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException("The data was changed by someone else. Reload and retry.", ex);
        }
        catch (DbUpdateException ex)
        {
            // Unique-index violations are the only expected failure here: the services check first,
            // so this only fires when two requests race to create the same key.
            throw new ConflictException("A record with the same key already exists.", ex);
        }
    }
}

/// <summary>Identifies who is making a change, for the audit log. Replaced by the authenticated user (ADR-0010).</summary>
public interface IActorAccessor
{
    string Actor { get; }
}

internal sealed class AnonymousActorAccessor : IActorAccessor
{
    public string Actor => "anonymous";
}
