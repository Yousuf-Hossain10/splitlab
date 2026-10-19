using Splitlab.Domain.Projects;

namespace Splitlab.Application.Results;

/// <param name="Events">Raw events read.</param>
/// <param name="ExposureStates">Per-user experiment rows now stored.</param>
/// <param name="MetricStates">Per-user metric rows now stored.</param>
public sealed record RebuildSummary(long Events, long ExposureStates, long MetricStates);

/// <summary>Recomputes rollups from raw events. Implementations are scoped to the current tenant.</summary>
public interface IRollupRebuilder
{
    Task<RebuildSummary> RebuildAsync(Guid environmentId, CancellationToken ct);
}

/// <summary>
/// Rollups are kept up to date as events are written, so this is for backfilling events stored before rollups existed
/// and for repairing them if they are ever suspected to be wrong. Safe to repeat; best run at a quiet time.
/// </summary>
public sealed class RollupService(
    IProjectRepository projects,
    IRollupRebuilder rebuilder,
    IAuditLog audit,
    IUnitOfWork unitOfWork)
{
    public async Task<RebuildSummary> RebuildAsync(string projectKey, string environmentKey, CancellationToken ct)
    {
        Project project = await projects.FindProjectAsync(projectKey, ct)
            ?? throw new NotFoundException($"Project '{projectKey}' was not found.");
        ProjectEnvironment environment = await projects.FindEnvironmentAsync(project.Id, environmentKey, ct)
            ?? throw new NotFoundException($"Environment '{environmentKey}' was not found.");

        RebuildSummary summary = await rebuilder.RebuildAsync(environment.Id, ct);

        audit.Record(project.Id, "rollups.rebuilt", nameof(ProjectEnvironment), environment.Id, $"{summary.Events} events");
        await unitOfWork.CommitAsync(ct);
        return summary;
    }
}
