using Microsoft.EntityFrameworkCore;
using Splitlab.Application.Results;
using Splitlab.Domain.Events;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Infrastructure.Events;

/// <summary>
/// Streams exposure and conversion rows for one environment. The context's tenant filter applies, so a request
/// scoped to one project can never read another project's events even if given another project's environment id.
/// Filters use equality only, so they behave the same on PostgreSQL and the SQLite used in tests.
/// </summary>
internal sealed class ExperimentEventReader(SplitlabDbContext db) : IExperimentEventReader
{
    public IAsyncEnumerable<ExposureRow> ReadExposuresAsync(Guid environmentId, string experimentKey, CancellationToken ct) =>
        db.Events.AsNoTracking()
            .Where(e => e.EnvironmentId == environmentId && e.Kind == EventKind.Exposure && e.ExperimentKey == experimentKey)
            .Select(e => new ExposureRow(e.UserId, e.VariationKey!, e.OccurredAt))
            .AsAsyncEnumerable();

    public IAsyncEnumerable<ConversionRow> ReadConversionsAsync(Guid environmentId, string metric, CancellationToken ct) =>
        db.Events.AsNoTracking()
            .Where(e => e.EnvironmentId == environmentId && e.Kind == EventKind.Conversion && e.Name == metric)
            .Select(e => new ConversionRow(e.UserId, e.OccurredAt))
            .AsAsyncEnumerable();
}
