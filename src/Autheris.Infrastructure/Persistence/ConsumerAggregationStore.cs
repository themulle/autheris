namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Architecture Phase 2 / Issue #114: Database operations for consumer aggregation counters and pseudonymous actor samples.
/// Dialect-compatible with SQLite, PostgreSQL, and SQL Server. Issue #286: the CONSUMER_APP_* tables are created by the
/// governance baseline migrations; this store never runs DDL.
/// </summary>
internal static class ConsumerAggregationStore
{
    public static async Task RecordDailyUsageAsync(
        DbConnection connection,
        ConsumerAggregationDialect dialect,
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string clientId,
        string version,
        long calls,
        long rowsReturned,
        long rowsMeasuredCalls,
        long rejAccess,
        long rejRateLimit,
        long rejCost,
        long rejOther,
        CancellationToken ct = default)
    {
        string sql = ConsumerAggregationSql.For(dialect).DailyUpsert;

        var parameters = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application),
            ("@client_id", clientId ?? string.Empty),
            ("@version", version ?? string.Empty),
            ("@calls", calls),
            ("@rows_returned", rowsReturned),
            ("@rows_measured_calls", rowsMeasuredCalls),
            ("@rej_access", rejAccess),
            ("@rej_rate_limit", rejRateLimit),
            ("@rej_cost", rejCost),
            ("@rej_other", rejOther)
        };

        await ExecuteAsync(connection, tx, sql, parameters, ct).ConfigureAwait(false);
    }

    public static async Task RecordActorSampleAsync(
        DbConnection connection,
        ConsumerAggregationDialect dialect,
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string actorHash,
        CancellationToken ct = default)
    {
        string sql = ConsumerAggregationSql.For(dialect).ActorInsert;

        var parameters = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application),
            ("@actor_hash", actorHash)
        };

        await ExecuteAsync(connection, tx, sql, parameters, ct).ConfigureAwait(false);
    }

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

    public static async Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        DbConnection connection,
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        string fromDay = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toDay = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        const string sql = @"
            SELECT day, application,
                   CAST(SUM(calls) AS BIGINT) as total_calls,
                   CAST(SUM(rows_returned) AS BIGINT) as total_rows,
                   CAST(SUM(rows_measured_calls) AS BIGINT) as total_measured,
                   CAST(SUM(rej_access) AS BIGINT) as total_rej_access,
                   CAST(SUM(rej_rate_limit) AS BIGINT) as total_rej_rate_limit,
                   CAST(SUM(rej_cost) AS BIGINT) as total_rej_cost,
                   CAST(SUM(rej_other) AS BIGINT) as total_rej_other
            FROM CONSUMER_APP_DAILY
            WHERE tenant_id = @tenant_id AND table_id = @table_id AND day >= @from_day AND day <= @to_day
            GROUP BY day, application
            ORDER BY day DESC, application ASC;
        ";

        var parameters = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@from_day", fromDay),
            ("@to_day", toDay)
        };

        var results = new List<ConsumerAppDailySummary>();

        await using (var cmd = Command(connection, tx, sql, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string day = reader.GetString(0);
                string app = reader.GetString(1);
                long calls = reader.GetInt64(2);
                long rows = reader.GetInt64(3);
                long measured = reader.GetInt64(4);
                long rejAcc = reader.GetInt64(5);
                long rejRate = reader.GetInt64(6);
                long rejCost = reader.GetInt64(7);
                long rejOther = reader.GetInt64(8);

                results.Add(new ConsumerAppDailySummary(
                    Application: app,
                    Day: day,
                    Calls: calls,
                    RowsReturned: rows,
                    RowsMeasuredCalls: measured,
                    Rejections: new RejectionBreakdown(rejAcc, rejRate, rejCost, rejOther),
                    Versions: Array.Empty<string>(),
                    QueryNames: Array.Empty<string>(),
                    DistinctActors: 0,
                    ActorSids: null
                ));
            }
        }

        // Hydrate versions, query names, and distinct actors for each (day, app)
        var enriched = new List<ConsumerAppDailySummary>(results.Count);
        foreach (var item in results)
        {
            var versions = await LoadVersionsAsync(connection, tx, tenantId, tableId, item.Day, item.Application, ct).ConfigureAwait(false);
            var queryNames = await LoadQueryNamesAsync(connection, tx, tenantId, tableId, item.Day, item.Application, ct).ConfigureAwait(false);
            var actorKeys = await LoadActorKeysAsync(connection, tx, tenantId, tableId, item.Day, item.Application, ct).ConfigureAwait(false);

            enriched.Add(item with
            {
                Versions = versions,
                QueryNames = queryNames,
                DistinctActors = actorKeys.Count,
                ActorKeys = actorKeys
            });
        }

        return enriched;
    }

    public static async Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        DbConnection connection,
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        string fromDay = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toDay = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        const string sql = @"
            SELECT application, CAST(COUNT(DISTINCT actor_hash) AS BIGINT) as actor_count
            FROM CONSUMER_APP_ACTOR_DAILY
            WHERE tenant_id = @tenant_id AND table_id = @table_id AND day >= @from_day AND day <= @to_day
            GROUP BY application;
        ";

        var parameters = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@from_day", fromDay),
            ("@to_day", toDay)
        };

        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = Command(connection, tx, sql, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string app = reader.GetString(0);
                int count = Convert.ToInt32(reader.GetInt64(1));
                dict[app] = count;
            }
        }

        return dict;
    }

    public static async Task<int> PurgeExpiredConsumerMetricsAsync(
        DbConnection connection,
        DbTransaction? tx,
        int retentionDays,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default)
    {
        // SEC-AUD114-05: Bound retention window to max 400 days
        int boundedDays = Math.Clamp(retentionDays, 1, 400);
        string cutoff = asOfUtc.AddDays(-boundedDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var p = new (string Name, object? Value)[] { ("@cutoff", cutoff) };

        int deleted = 0;
        deleted += await ExecuteAsync(connection, tx, "DELETE FROM CONSUMER_APP_DAILY WHERE day < @cutoff;", p, ct).ConfigureAwait(false);
        deleted += await ExecuteAsync(connection, tx, "DELETE FROM CONSUMER_APP_ACTOR_DAILY WHERE day < @cutoff;", p, ct).ConfigureAwait(false);
        deleted += await ExecuteAsync(connection, tx, "DELETE FROM CONSUMER_APP_QUERYNAME_DAILY WHERE day < @cutoff;", p, ct).ConfigureAwait(false);

        return deleted;
    }

    private static async Task<IReadOnlyList<string>> LoadVersionsAsync(
        DbConnection connection, DbTransaction? tx, TenantId tenantId, TableIdentifier tableId, string day, string application, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT version FROM CONSUMER_APP_DAILY
            WHERE tenant_id = @tenant_id AND table_id = @table_id AND day = @day AND application = @application AND version <> ''
            ORDER BY version ASC;
        ";
        var p = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application)
        };

        var list = new List<string>();
        await using var cmd = Command(connection, tx, sql, p);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    private static async Task<IReadOnlyList<string>> LoadQueryNamesAsync(
        DbConnection connection, DbTransaction? tx, TenantId tenantId, TableIdentifier tableId, string day, string application, CancellationToken ct)
    {
        const string sql = @"
            SELECT query_name FROM CONSUMER_APP_QUERYNAME_DAILY
            WHERE tenant_id = @tenant_id AND table_id = @table_id AND day = @day AND application = @application
            ORDER BY calls DESC;
        ";
        var p = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application)
        };

        var list = new List<string>();
        await using var cmd = Command(connection, tx, sql, p);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    /// <summary>The distinct (HMAC-pseudonymised) actor keys of one (day, application) cell.</summary>
    private static async Task<IReadOnlySet<string>> LoadActorKeysAsync(
        DbConnection connection, DbTransaction? tx, TenantId tenantId, TableIdentifier tableId, string day, string application, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT actor_hash FROM CONSUMER_APP_ACTOR_DAILY
            WHERE tenant_id = @tenant_id AND table_id = @table_id AND day = @day AND application = @application;
        ";
        var p = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application)
        };

        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = Command(connection, tx, sql, p);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
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
