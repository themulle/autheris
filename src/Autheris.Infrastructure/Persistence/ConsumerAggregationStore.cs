namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Database operations for <c>CONSUMER_APP_QUERYNAME_DAILY</c>.
/// Dialect-compatible with SQLite, PostgreSQL, and SQL Server. Issue #286: the table is created by the
/// governance baseline migrations; this store never runs DDL.
/// </summary>
internal static class ConsumerAggregationStore
{
    public static async Task RecordQueryNameAsync(
        DbConnection connection,
        ConsumerAggregationDialect dialect,
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string queryName,
        long calls,
        CancellationToken ct = default)
    {
        string sql = ConsumerAggregationSql.For(dialect).QueryNameUpsert;

        var parameters = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application),
            ("@query_name", queryName),
            ("@calls", calls)
        };

        await ExecuteAsync(connection, tx, sql, parameters, ct).ConfigureAwait(false);
    }

    public static async Task<int> PurgeExpiredConsumerMetricsAsync(
        DbConnection connection,
        DbTransaction? tx,
        int retentionDays,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default)
    {
        // Bound retention window to max 400 days
        int boundedDays = Math.Clamp(retentionDays, 1, 400);
        string cutoff = asOfUtc.AddDays(-boundedDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var p = new (string Name, object? Value)[] { ("@cutoff", cutoff) };
        return await ExecuteAsync(connection, tx, "DELETE FROM CONSUMER_APP_QUERYNAME_DAILY WHERE day < @cutoff;", p, ct).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? tx, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
