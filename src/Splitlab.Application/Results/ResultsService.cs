using Splitlab.Domain;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;
using Splitlab.Domain.Statistics;

namespace Splitlab.Application.Results;

public readonly record struct ExposureRow(string UserId, string VariationKey, DateTimeOffset At);

public readonly record struct ConversionRow(string UserId, DateTimeOffset At);

/// <summary>
/// Streams the raw events an analysis needs. Rows are streamed so a large experiment is not loaded as one list.
/// Implementations are scoped to the current tenant.
/// </summary>
public interface IExperimentEventReader
{
    IAsyncEnumerable<ExposureRow> ReadExposuresAsync(Guid environmentId, string experimentKey, CancellationToken ct);

    IAsyncEnumerable<ConversionRow> ReadConversionsAsync(Guid environmentId, string metric, CancellationToken ct);
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

/// <summary>
/// Computes an experiment's results straight from raw events (ADR-0006). Reading raw events is the interim approach;
/// ADR-0004 plans rollup tables so dashboard queries do not scan them.
/// </summary>
public sealed class ResultsService(
    IProjectRepository projects,
    IExperimentRepository experiments,
    IExperimentEventReader reader,
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

        var aggregator = new ResultsAggregator(variations.Select(v => v.Key).ToList());
        await foreach (ExposureRow row in reader.ReadExposuresAsync(environment.Id, experiment.Key, ct))
        {
            aggregator.AddExposure(row.UserId, row.VariationKey, row.At);
        }

        await foreach (ConversionRow row in reader.ReadConversionsAsync(environment.Id, validMetric, ct))
        {
            aggregator.AddConversion(row.UserId, row.At);
        }

        AggregatedCounts counts = aggregator.Complete();
        var data = variations
            .Zip(counts.Counts, (v, c) => new VariationData(v.Key, v.TrafficBasisPoints, c.Exposed, c.Converted))
            .ToList();

        return new ExperimentResults(
            experiment.Key,
            validMetric,
            experiment.Status.ToString(),
            ExperimentAnalyzer.Analyze(data, options),
            counts.ExcludedUsers,
            counts.UnknownVariationUsers,
            clock.GetUtcNow());
    }
}
