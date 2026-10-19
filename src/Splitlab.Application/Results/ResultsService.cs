using Splitlab.Domain;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;
using Splitlab.Domain.Statistics;

namespace Splitlab.Application.Results;

/// <summary>Unique users who were exposed to a variation, and how many of them converted.</summary>
public sealed record VariationCount(string VariationKey, long Exposed, long Converted);

/// <param name="ByVariation">Counts for users in exactly one variation. May include keys the experiment does not define.</param>
/// <param name="ExcludedUsers">Users exposed to more than one variation.</param>
public sealed record ExperimentCounts(IReadOnlyList<VariationCount> ByVariation, long ExcludedUsers);

/// <summary>
/// Reads an experiment's user counts from the rollup tables (ADR-0004). Implementations are scoped to the current tenant.
/// </summary>
public interface IExperimentCountsReader
{
    Task<ExperimentCounts> ReadAsync(Guid environmentId, string experimentKey, string metric, CancellationToken ct);
}

/// <param name="ExcludedUsers">Users exposed to more than one variation, left out of every figure.</param>
/// <param name="UnknownVariationUsers">Users exposed only to a variation the experiment does not define.</param>
public sealed record ExperimentResults(
    string Experiment,
    string Metric,
    string Status,
    ExperimentAnalysis Analysis,
    long ExcludedUsers,
    long UnknownVariationUsers,
    DateTimeOffset GeneratedAt);

/// <summary>Computes an experiment's results from the rollups (ADR-0004, ADR-0006).</summary>
public sealed class ResultsService(
    IProjectRepository projects,
    IExperimentRepository experiments,
    IExperimentCountsReader counts,
    AnalysisOptions options,
    TimeProvider clock)
{
    /// <param name="metric">The conversion event name to measure, such as <c>purchase</c>.</param>
    /// <exception cref="DomainException">The metric is invalid or the experiment has fewer than two variations.</exception>
    public async Task<ExperimentResults> GetAsync(
        string projectKey, string environmentKey, string experimentKey, string? metric, CancellationToken ct)
    {
        string validMetric = Keys.Validate(metric, "metric");

        Project project = await projects.FindProjectAsync(projectKey, ct)
            ?? throw new NotFoundException($"Project '{projectKey}' was not found.");
        ProjectEnvironment environment = await projects.FindEnvironmentAsync(project.Id, environmentKey, ct)
            ?? throw new NotFoundException($"Environment '{environmentKey}' was not found.");
        Experiment experiment = await experiments.FindAsync(environment.Id, experimentKey, ct)
            ?? throw new NotFoundException($"Experiment '{experimentKey}' was not found.");

        IReadOnlyList<Variation> variations = experiment.Variations;
        if (variations.Count < 2)
        {
            throw new DomainException("Results need an experiment with at least two variations.");
        }

        ExperimentCounts observed = await counts.ReadAsync(environment.Id, experiment.Key, validMetric, ct);
        var byKey = observed.ByVariation.ToDictionary(c => c.VariationKey, StringComparer.Ordinal);

        var data = variations
            .Select(v => byKey.TryGetValue(v.Key, out VariationCount? c)
                ? new VariationData(v.Key, v.TrafficBasisPoints, c.Exposed, c.Converted)
                : new VariationData(v.Key, v.TrafficBasisPoints, 0, 0))
            .ToList();

        var defined = variations.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        long unknown = observed.ByVariation.Where(c => !defined.Contains(c.VariationKey)).Sum(c => c.Exposed);

        return new ExperimentResults(
            experiment.Key,
            validMetric,
            experiment.Status.ToString(),
            ExperimentAnalyzer.Analyze(data, options),
            observed.ExcludedUsers,
            unknown,
            clock.GetUtcNow());
    }
}
