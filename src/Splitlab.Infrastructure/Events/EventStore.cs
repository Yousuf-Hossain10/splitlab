using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Splitlab.Application.Events;
using Splitlab.Domain.Events;
using Splitlab.Domain.Statistics;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Infrastructure.Events;

/// <summary>
/// Writes event batches, skipping events that are already stored. This bypasses EF change tracking on purpose:
/// the writer serves many projects at once and has no request tenant (ADR-0004). The per-user rollups behind results are
/// updated in the same transaction, so they can never disagree with the stored events.
/// PostgreSQL uses COPY into a staging table then <c>INSERT ... ON CONFLICT DO NOTHING</c>; other providers
/// (the SQLite used in tests) use one conflict-ignoring insert per event inside a transaction.
/// </summary>
internal sealed class EventStore(SplitlabDbContext db) : IEventStore
{
    public Task<int> InsertAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct) =>
        db.Database.IsNpgsql() ? CopyAsync(batch, ct) : InsertRowsAsync(batch, ct);

    private async Task<int> CopyAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);

            await using (var create = new NpgsqlCommand(
                "CREATE TEMP TABLE events_stage (LIKE events INCLUDING DEFAULTS) ON COMMIT DROP", connection, tx))
            {
                await create.ExecuteNonQueryAsync(ct);
            }

            await using (NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
                $"COPY events_stage ({Columns}) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (EventRecord e in batch)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(e.ProjectId, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(e.EnvironmentId, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(e.EventId, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(e.OccurredAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
                    await writer.WriteAsync(e.Kind.ToString(), NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(e.UserId, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, e.ExperimentKey, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, e.VariationKey, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, e.Name, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, e.Value, NpgsqlDbType.Double, ct);
                    await writer.WriteAsync(e.ReceivedAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
                }

                await writer.CompleteAsync(ct);
            }

            int inserted;
            await using (var insert = new NpgsqlCommand(
                $"INSERT INTO events ({Columns}) SELECT {Columns} FROM events_stage ON CONFLICT DO NOTHING", connection, tx))
            {
                inserted = await insert.ExecuteNonQueryAsync(ct);
            }

            await RollupWriter.ApplyAsync(connection, tx, RollupBatch.From(batch), ct);

            await tx.CommitAsync(ct);
            return inserted;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<int> InsertRowsAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int inserted = 0;

        foreach (EventRecord e in batch)
        {
            inserted += await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO events ("ProjectId", "EnvironmentId", "EventId", "OccurredAt", "Kind", "UserId", "ExperimentKey", "VariationKey", "Name", "Value", "ReceivedAt")
                VALUES ({e.ProjectId}, {e.EnvironmentId}, {e.EventId}, {e.OccurredAt}, {e.Kind.ToString()}, {e.UserId}, {e.ExperimentKey}, {e.VariationKey}, {e.Name}, {e.Value}, {e.ReceivedAt})
                ON CONFLICT DO NOTHING
                """,
                ct);
        }

        await RollupWriter.ApplyAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), RollupBatch.From(batch), ct);

        await tx.CommitAsync(ct);
        return inserted;
    }

    private const string Columns =
        "\"ProjectId\", \"EnvironmentId\", \"EventId\", \"OccurredAt\", \"Kind\", \"UserId\", \"ExperimentKey\", \"VariationKey\", \"Name\", \"Value\", \"ReceivedAt\"";

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(ct);
        }
        else
        {
            await writer.WriteAsync(value, type, ct);
        }
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter writer, double? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is { } v)
        {
            await writer.WriteAsync(v, type, ct);
        }
        else
        {
            await writer.WriteNullAsync(ct);
        }
    }
}
