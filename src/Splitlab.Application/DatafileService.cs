using System.Text.Json;
using Splitlab.Domain.Projects;

namespace Splitlab.Application;

/// <summary>The datafile body and its strong ETag, or a not-modified marker.</summary>
public sealed record DatafileResult(string ETag, string? Json)
{
    public bool NotModified => Json is null;
}

/// <summary>Builds the versioned JSON datafile the SDK polls (ADR-0007). Only running experiments are published.</summary>
public sealed class DatafileService(IProjectRepository projects, IExperimentRepository experiments)
{
    /// <summary>Must match <c>DatafileParser.SupportedSchemaVersion</c> in the SDK.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="environmentKey">Environment named in the URL; looked up within the caller's project only.</param>
    /// <param name="allowedEnvironmentId">The one environment the caller's SDK key is scoped to.</param>
    /// <param name="ifNoneMatch">The ETag the caller already holds, if any.</param>
    public async Task<DatafileResult> GetAsync(
        string environmentKey, Guid allowedEnvironmentId, string? ifNoneMatch, CancellationToken ct)
    {
        ProjectEnvironment? environment = await projects.FindEnvironmentByKeyAsync(environmentKey, ct);
        if (environment is null || environment.Id != allowedEnvironmentId)
        {
            throw new NotFoundException($"Environment '{environmentKey}' was not found.");
        }

        string etag = $"\"{environment.ConfigVersion}\"";
        if (ifNoneMatch == etag)
        {
            return new DatafileResult(etag, null);
        }

        var running = await experiments.ListRunningAsync(environment.Id, ct);
        var document = new
        {
            schemaVersion = SchemaVersion,
            version = environment.ConfigVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            experiments = running.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new
            {
                key = e.Key,
                variations = e.Variations.Select(v => new { key = v.Key, trafficBasisPoints = v.TrafficBasisPoints }),
            }),
        };

        return new DatafileResult(etag, JsonSerializer.Serialize(document, JsonOptions));
    }
}
