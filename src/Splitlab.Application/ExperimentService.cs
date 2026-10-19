using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Application;

public sealed class ExperimentService(
    IProjectRepository projects,
    IExperimentRepository experiments,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<Experiment> CreateAsync(
        string projectKey,
        string environmentKey,
        string? key,
        string? name,
        IEnumerable<VariationSpec> variations,
        CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        var experiment = Experiment.Create(project.Id, environment.Id, key, name, variations, clock.GetUtcNow());

        if (await experiments.FindAsync(environment.Id, experiment.Key, ct) is not null)
        {
            throw new ConflictException($"Experiment '{experiment.Key}' already exists in this environment.");
        }

        experiments.Add(experiment);
        audit.Record(project.Id, "experiment.created", nameof(Experiment), experiment.Id, experiment.Key);
        await unitOfWork.CommitAsync(ct);
        return experiment;
    }

    public async Task<IReadOnlyList<Experiment>> ListAsync(string projectKey, string environmentKey, CancellationToken ct)
    {
        (_, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        return await experiments.ListAsync(environment.Id, ct);
    }

    public async Task<Experiment> GetAsync(string projectKey, string environmentKey, string key, CancellationToken ct)
    {
        (_, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        return await FindAsync(environment, key, ct);
    }

    /// <param name="expectedVersion">The version the caller last saw; a mismatch means someone else changed it first.</param>
    public async Task<Experiment> ReplaceVariationsAsync(
        string projectKey,
        string environmentKey,
        string key,
        int expectedVersion,
        IEnumerable<VariationSpec> variations,
        CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        Experiment experiment = await FindAsync(environment, key, ct);
        EnsureVersion(experiment, expectedVersion);

        experiment.ReplaceVariations(variations, clock.GetUtcNow());
        audit.Record(project.Id, "experiment.variations_replaced", nameof(Experiment), experiment.Id, experiment.Key);
        await unitOfWork.CommitAsync(ct);
        return experiment;
    }

    public async Task<Experiment> StartAsync(
        string projectKey, string environmentKey, string key, int expectedVersion, CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        Experiment experiment = await FindAsync(environment, key, ct);
        EnsureVersion(experiment, expectedVersion);

        experiment.Start(clock.GetUtcNow());
        environment.BumpConfigVersion();
        audit.Record(project.Id, "experiment.started", nameof(Experiment), experiment.Id, experiment.Key);
        await unitOfWork.CommitAsync(ct);
        return experiment;
    }

    public async Task<Experiment> StopAsync(
        string projectKey, string environmentKey, string key, int expectedVersion, CancellationToken ct)
    {
        (Project project, ProjectEnvironment environment) = await ResolveAsync(projectKey, environmentKey, ct);
        Experiment experiment = await FindAsync(environment, key, ct);
        EnsureVersion(experiment, expectedVersion);

        experiment.Stop(clock.GetUtcNow());
        environment.BumpConfigVersion();
        audit.Record(project.Id, "experiment.stopped", nameof(Experiment), experiment.Id, experiment.Key);
        await unitOfWork.CommitAsync(ct);
        return experiment;
    }

    private async Task<(Project, ProjectEnvironment)> ResolveAsync(string projectKey, string environmentKey, CancellationToken ct)
    {
        Project project = await projects.FindProjectAsync(projectKey, ct)
            ?? throw new NotFoundException($"Project '{projectKey}' was not found.");
        ProjectEnvironment environment = await projects.FindEnvironmentAsync(project.Id, environmentKey, ct)
            ?? throw new NotFoundException($"Environment '{environmentKey}' was not found.");
        return (project, environment);
    }

    private async Task<Experiment> FindAsync(ProjectEnvironment environment, string key, CancellationToken ct) =>
        await experiments.FindAsync(environment.Id, key, ct)
            ?? throw new NotFoundException($"Experiment '{key}' was not found.");

    private static void EnsureVersion(Experiment experiment, int expectedVersion)
    {
        if (experiment.Version != expectedVersion)
        {
            throw new ConflictException(
                $"Experiment '{experiment.Key}' is at version {experiment.Version}, not {expectedVersion}. Reload and retry.");
        }
    }
}
