using Microsoft.EntityFrameworkCore;
using Splitlab.Application;
using Splitlab.Domain.Audit;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Infrastructure.Persistence;

internal sealed class TenantContext : ITenantContext
{
    public Guid? ProjectId { get; private set; }

    public void Use(Guid projectId)
    {
        if (ProjectId is { } current && current != projectId)
        {
            throw new TenantIsolationException("A request cannot switch to a different project once scoped.");
        }

        ProjectId = projectId;
    }
}

/// <summary>The only place that deliberately bypasses the tenant filters: resolving who may enter which tenant.</summary>
internal sealed class AccessRepository(SplitlabDbContext db) : IAccessRepository
{
    public async Task<ProjectAccess?> GetAccessAsync(string projectKey, string subject, CancellationToken ct)
    {
        var row = await (
            from member in db.Members.IgnoreQueryFilters()
            join project in db.Projects.IgnoreQueryFilters() on member.ProjectId equals project.Id
            where project.Key == projectKey && member.Subject == subject
            select new { project.Id, member.Role }).FirstOrDefaultAsync(ct);

        return row is null ? null : new ProjectAccess(row.Id, row.Role);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsForAsync(string subject, CancellationToken ct) =>
        await (
            from member in db.Members.IgnoreQueryFilters()
            join project in db.Projects.IgnoreQueryFilters().AsNoTracking() on member.ProjectId equals project.Id
            where member.Subject == subject
            orderby project.Key
            select project).ToListAsync(ct);

    public Task<bool> ProjectKeyExistsAsync(string key, CancellationToken ct) =>
        db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Key == key, ct);

    public Task<ApiKey?> FindApiKeyByPrefixAsync(string prefix, CancellationToken ct) =>
        db.ApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.Prefix == prefix, ct);
}

internal sealed class MemberRepository(SplitlabDbContext db) : IMemberRepository
{
    public Task<ProjectMember?> FindAsync(string subject, CancellationToken ct) =>
        db.Members.FirstOrDefaultAsync(m => m.Subject == subject, ct);

    public async Task<IReadOnlyList<ProjectMember>> ListAsync(CancellationToken ct) =>
        await db.Members.AsNoTracking().OrderBy(m => m.Subject).ToListAsync(ct);

    public Task<int> CountAdminsAsync(CancellationToken ct) =>
        db.Members.CountAsync(m => m.Role == ProjectRole.Admin, ct);

    public void Add(ProjectMember member) => db.Members.Add(member);

    public void Remove(ProjectMember member) => db.Members.Remove(member);
}

internal sealed class ApiKeyRepository(SplitlabDbContext db) : IApiKeyRepository
{
    public Task<ApiKey?> FindAsync(Guid id, CancellationToken ct) =>
        db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);

    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid environmentId, CancellationToken ct) =>
        await db.ApiKeys.AsNoTracking().Where(k => k.EnvironmentId == environmentId).OrderBy(k => k.Prefix).ToListAsync(ct);

    public void Add(ApiKey key) => db.ApiKeys.Add(key);
}

internal sealed class ProjectRepository(SplitlabDbContext db) : IProjectRepository
{
    public Task<Project?> FindProjectAsync(string key, CancellationToken ct) =>
        db.Projects.FirstOrDefaultAsync(p => p.Key == key, ct);

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

internal sealed class AuditLog(SplitlabDbContext db, TimeProvider clock, ICurrentUser user) : IAuditLog
{
    public void Record(Guid projectId, string action, string entityType, Guid entityId, string? detail = null) =>
        db.AuditEntries.Add(AuditEntry.Create(projectId, user.Actor, action, entityType, entityId, detail, clock.GetUtcNow()));
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
