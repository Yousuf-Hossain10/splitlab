using Splitlab.Domain.Projects;

namespace Splitlab.Application;

public sealed class ProjectService(
    IProjectRepository projects,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<Project> CreateProjectAsync(string? key, string? name, CancellationToken ct)
    {
        var project = Project.Create(key, name, clock.GetUtcNow());
        if (await projects.FindProjectAsync(project.Key, ct) is not null)
        {
            throw new ConflictException($"Project '{project.Key}' already exists.");
        }

        projects.Add(project);
        audit.Record(project.Id, "project.created", nameof(Project), project.Id, project.Key);
        await unitOfWork.CommitAsync(ct);
        return project;
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => projects.ListProjectsAsync(ct);

    public async Task<Project> GetProjectAsync(string key, CancellationToken ct) =>
        await projects.FindProjectAsync(key, ct) ?? throw new NotFoundException($"Project '{key}' was not found.");

    public async Task<ProjectEnvironment> CreateEnvironmentAsync(
        string projectKey, string? key, string? name, CancellationToken ct)
    {
        Project project = await GetProjectAsync(projectKey, ct);
        var environment = ProjectEnvironment.Create(project.Id, key, name, clock.GetUtcNow());

        if (await projects.FindEnvironmentByKeyAsync(environment.Key, ct) is not null)
        {
            throw new ConflictException($"Environment key '{environment.Key}' is already in use.");
        }

        projects.Add(environment);
        audit.Record(project.Id, "environment.created", nameof(ProjectEnvironment), environment.Id, environment.Key);
        await unitOfWork.CommitAsync(ct);
        return environment;
    }

    public async Task<IReadOnlyList<ProjectEnvironment>> ListEnvironmentsAsync(string projectKey, CancellationToken ct)
    {
        Project project = await GetProjectAsync(projectKey, ct);
        return await projects.ListEnvironmentsAsync(project.Id, ct);
    }
}
