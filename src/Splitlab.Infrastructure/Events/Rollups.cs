using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Application;
using Splitlab.Application.Results;
using Splitlab.Domain.Events;
using Splitlab.Domain.Statistics;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Infrastructure.Events;

/// <summary>
/// Folds a <see cref="RollupBatch"/> into the stored rollup rows with upserts. The SQL is the same on PostgreSQL and
/// SQLite, and each merge is order-independent and repeatable, so a retried or reordered batch cannot change the result.
/// It runs inside the transaction that stores the events, so rollups and events never disagree.
/// </summary>
internal static class RollupWriter
{
    // Existing row is referenced by table name, the incoming row as "excluded". Plain CASE/OR expressions only, so the
    // statement means the same on both databases.
    private const string ExposureUpsert = """
        INSERT INTO experiment_user_states ("ProjectId", "EnvironmentId", "ExperimentKey", "UserId", "VariationKey", "FirstExposureMs", "Conflicted")
        VALUES (@project, @environment, @experiment, @user, @variation, @first, @conflicted)
        ON CONFLICT ("EnvironmentId", "ExperimentKey", "UserId") DO UPDATE SET
            "FirstExposureMs" = CASE WHEN excluded."FirstExposureMs" < experiment_user_states."FirstExposureMs"
                                     THEN excluded."FirstExposureMs" ELSE experiment_user_states."FirstExposureMs" END,
            "Conflicted" = (experiment_user_states."Conflicted" OR excluded."Conflicted"
                            OR experiment_user_states."VariationKey" <> excluded."VariationKey")
        """;

    private const string ConversionUpsert = """
        INSERT INTO metric_user_states ("ProjectId", "EnvironmentId", "Metric", "UserId", "LastConversionMs")
        VALUES (@project, @environment, @metric, @user, @last)
        ON CONFLICT ("EnvironmentId", "Metric", "UserId") DO UPDATE SET
            "LastConversionMs" = CASE WHEN excluded."LastConversionMs" > metric_user_states."LastConversionMs"
                                      THEN excluded."LastConversionMs" ELSE metric_user_states."LastConversionMs" END
        """;

    public static async Task ApplyAsync(DbConnection connection, DbTransaction transaction, RollupBatch batch, CancellationToken ct)
    {
        if (batch.Exposures.Count > 0)
        {
            await using DbCommand cmd = Command(connection, transaction, ExposureUpsert);
            DbParameter project = Add(cmd, "@project", DbType.Guid);
            DbParameter environment = Add(cmd, "@environment", DbType.Guid);
            DbParameter experiment = Add(cmd, "@experiment", DbType.String);
            DbParameter user = Add(cmd, "@user", DbType.String);
            DbParameter variation = Add(cmd, "@variation", DbType.String);
            DbParameter first = Add(cmd, "@first", DbType.Int64);
            DbParameter conflicted = Add(cmd, "@conflicted", DbType.Boolean);

            foreach (ExposureDelta d in batch.Exposures)
            {
                project.Value = d.ProjectId;
                environment.Value = d.EnvironmentId;
                experiment.Value = d.ExperimentKey;
                user.Value = d.UserId;
                variation.Value = d.VariationKey;
                first.Value = d.FirstExposureMs;
                conflicted.Value = d.Conflicted;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (batch.Conversions.Count > 0)
        {
            await using DbCommand cmd = Command(connection, transaction, ConversionUpsert);
            DbParameter project = Add(cmd, "@project", DbType.Guid);
            DbParameter environment = Add(cmd, "@environment", DbType.Guid);
            DbParameter metric = Add(cmd, "@metric", DbType.String);
            DbParameter user = Add(cmd, "@user", DbType.String);
            DbParameter last = Add(cmd, "@last", DbType.Int64);

            foreach (ConversionDelta d in batch.Conversions)
            {
                project.Value = d.ProjectId;
                environment.Value = d.EnvironmentId;
                metric.Value = d.Metric;
                user.Value = d.UserId;
                last.Value = d.LastConversionMs;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static DbParameter Add(DbCommand cmd, string name, DbType type)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        cmd.Parameters.Add(p);
        return p;
    }
}

/// <summary>Counts unique users per variation from the rollup rows, in SQL. The context's tenant filter applies.</summary>
internal sealed class ExperimentCountsReader(SplitlabDbContext db) : IExperimentCountsReader
{
    public async Task<ExperimentCounts> ReadAsync(Guid environmentId, string experimentKey, string metric, CancellationToken ct)
    {
        IQueryable<ExperimentUserState> states = db.ExperimentUserStates.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId && s.ExperimentKey == experimentKey);
        IQueryable<ExperimentUserState> clean = states.Where(s => !s.Conflicted);

        var exposed = await clean
            .GroupBy(s => s.VariationKey)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // A conversion counts only if it happened at or after the user's first exposure (ADR-0006).
        var converted = await (
            from s in clean
            join m in db.MetricUserStates.AsNoTracking().Where(m => m.EnvironmentId == environmentId && m.Metric == metric)
                on s.UserId equals m.UserId
            where m.LastConversionMs >= s.FirstExposureMs
            group s by s.VariationKey into g
            select new { Key = g.Key, Count = g.Count() }).ToListAsync(ct);

        int excluded = await states.CountAsync(s => s.Conflicted, ct);

        var convertedByKey = converted.ToDictionary(c => c.Key, c => (long)c.Count, StringComparer.Ordinal);
        return new ExperimentCounts(
            exposed.Select(e => new VariationCount(e.Key, e.Count, convertedByKey.GetValueOrDefault(e.Key))).ToList(),
            excluded);
    }
}

/// <summary>Deletes an environment's rollup rows and rebuilds them by replaying its raw events through the same upsert path.</summary>
internal sealed class RollupRebuilder(SplitlabDbContext db, IServiceScopeFactory scopes, ITenantContext tenant) : IRollupRebuilder
{
    private const int ChunkSize = 1_000;

    public async Task<RebuildSummary> RebuildAsync(Guid environmentId, CancellationToken ct)
    {
        Guid projectId = tenant.ProjectId ?? throw new InvalidOperationException("Rebuilding rollups needs a project scope.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        DbConnection connection = db.Database.GetDbConnection();
        DbTransaction dbTransaction = tx.GetDbTransaction();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM experiment_user_states WHERE \"EnvironmentId\" = {environmentId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM metric_user_states WHERE \"EnvironmentId\" = {environmentId}", ct);

        // Events are streamed on a second context (and so a second connection on PostgreSQL), because a connection
        // cannot run the upserts while it is still reading.
        await using AsyncServiceScope readScope = scopes.CreateAsyncScope();
        readScope.ServiceProvider.GetRequiredService<ITenantContext>().Use(projectId);
        var reader = readScope.ServiceProvider.GetRequiredService<SplitlabDbContext>();

        long events = 0;
        var chunk = new List<EventRecord>(ChunkSize);
        await foreach (EventRecord e in reader.Events.AsNoTracking().Where(e => e.EnvironmentId == environmentId).AsAsyncEnumerable().WithCancellation(ct))
        {
            chunk.Add(e);
            events++;
            if (chunk.Count == ChunkSize)
            {
                await RollupWriter.ApplyAsync(connection, dbTransaction, RollupBatch.From(chunk), ct);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            await RollupWriter.ApplyAsync(connection, dbTransaction, RollupBatch.From(chunk), ct);
        }

        long exposureStates = await db.ExperimentUserStates.CountAsync(s => s.EnvironmentId == environmentId, ct);
        long metricStates = await db.MetricUserStates.CountAsync(s => s.EnvironmentId == environmentId, ct);
        await tx.CommitAsync(ct);
        return new RebuildSummary(events, exposureStates, metricStates);
    }
}
