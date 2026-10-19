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
/// Dialect-compatible with SQLite, PostgreSQL, and SQL Server.
/// </summary>
internal static class ConsumerAggregationStore
{
    public static async Task EnsureTablesCreatedAsync(DbConnection connection, DbTransaction? tx = null, CancellationToken ct = default)
    {
        const string ddl = @"
            CREATE TABLE IF NOT EXISTS CONSUMER_APP_DAILY (
                tenant_id TEXT NOT NULL,
                table_id TEXT NOT NULL,
                day TEXT NOT NULL,
                application TEXT NOT NULL,
                client_id TEXT NOT NULL DEFAULT '',
                version TEXT NOT NULL DEFAULT '',
                calls INTEGER NOT NULL DEFAULT 0,
                rows_returned INTEGER NOT NULL DEFAULT 0,
                rows_measured_calls INTEGER NOT NULL DEFAULT 0,
                rej_access INTEGER NOT NULL DEFAULT 0,
                rej_rate_limit INTEGER NOT NULL DEFAULT 0,
                rej_cost INTEGER NOT NULL DEFAULT 0,
                rej_other INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (tenant_id, table_id, day, application, client_id, version)
            );

            CREATE TABLE IF NOT EXISTS CONSUMER_APP_ACTOR_DAILY (
                tenant_id TEXT NOT NULL,
                table_id TEXT NOT NULL,
                day TEXT NOT NULL,
                application TEXT NOT NULL,
                actor_hash TEXT NOT NULL,
                PRIMARY KEY (tenant_id, table_id, day, application, actor_hash)
            );

            CREATE TABLE IF NOT EXISTS CONSUMER_APP_QUERYNAME_DAILY (
                tenant_id TEXT NOT NULL,
                table_id TEXT NOT NULL,
                day TEXT NOT NULL,
                application TEXT NOT NULL,
                query_name TEXT NOT NULL,
                calls INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (tenant_id, table_id, day, application, query_name)
            );
        ";

        await ExecuteAsync(connection, tx, ddl, Array.Empty<(string, object?)>(), ct).ConfigureAwait(false);
    }

    public static async Task RecordDailyUsageAsync(
        DbConnection connection,
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
        await EnsureTablesCreatedAsync(connection, tx, ct).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO CONSUMER_APP_DAILY (
                tenant_id, table_id, day, application, client_id, version,
                calls, rows_returned, rows_measured_calls,
                rej_access, rej_rate_limit, rej_cost, rej_other
            ) VALUES (
                @tenant_id, @table_id, @day, @application, @client_id, @version,
                @calls, @rows_returned, @rows_measured_calls,
                @rej_access, @rej_rate_limit, @rej_cost, @rej_other
            )
            ON CONFLICT(tenant_id, table_id, day, application, client_id, version) DO UPDATE SET
                calls = calls + excluded.calls,
                rows_returned = rows_returned + excluded.rows_returned,
                rows_measured_calls = rows_measured_calls + excluded.rows_measured_calls,
                rej_access = rej_access + excluded.rej_access,
                rej_rate_limit = rej_rate_limit + excluded.rej_rate_limit,
                rej_cost = rej_cost + excluded.rej_cost,
                rej_other = rej_other + excluded.rej_other;
        ";

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
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string actorHash,
        CancellationToken ct = default)
    {
        await EnsureTablesCreatedAsync(connection, tx, ct).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO CONSUMER_APP_ACTOR_DAILY (tenant_id, table_id, day, application, actor_hash)
            VALUES (@tenant_id, @table_id, @day, @application, @actor_hash)
            ON CONFLICT(tenant_id, table_id, day, application, actor_hash) DO NOTHING;
        ";

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
        DbTransaction? tx,
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string queryName,
        long calls,
        CancellationToken ct = default)
    {
        await EnsureTablesCreatedAsync(connection, tx, ct).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO CONSUMER_APP_QUERYNAME_DAILY (tenant_id, table_id, day, application, query_name, calls)
            VALUES (@tenant_id, @table_id, @day, @application, @query_name, @calls)
            ON CONFLICT(tenant_id, table_id, day, application, query_name) DO UPDATE SET
                calls = calls + excluded.calls;
        ";

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
        await EnsureTablesCreatedAsync(connection, tx, ct).ConfigureAwait(false);

        string fromDay = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toDay = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        const string sql = @"
            SELECT day, application,
                   SUM(calls) as total_calls,
                   SUM(rows_returned) as total_rows,
                   SUM(rows_measured_calls) as total_measured,
                   SUM(rej_access) as total_rej_access,
                   SUM(rej_rate_limit) as total_rej_rate_limit,
                   SUM(rej_cost) as total_rej_cost,
                   SUM(rej_other) as total_rej_other
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
            int actorCount = await LoadDistinctActorCountAsync(connection, tx, tenantId, tableId, item.Day, item.Application, ct).ConfigureAwait(false);

            enriched.Add(item with
            {
                Versions = versions,
                QueryNames = queryNames,
                DistinctActors = actorCount
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
        await EnsureTablesCreatedAsync(connection, tx, ct).ConfigureAwait(false);

        string fromDay = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toDay = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        const string sql = @"
            SELECT application, COUNT(DISTINCT actor_hash) as actor_count
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
        await EnsureTablesCreatedAsync(connection, tx, ct).ConfigureAwait(false);

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

    private static async Task<int> LoadDistinctActorCountAsync(
        DbConnection connection, DbTransaction? tx, TenantId tenantId, TableIdentifier tableId, string day, string application, CancellationToken ct)
    {
        const string sql = @"
            SELECT COUNT(DISTINCT actor_hash) FROM CONSUMER_APP_ACTOR_DAILY
            WHERE tenant_id = @tenant_id AND table_id = @table_id AND day = @day AND application = @application;
        ";
        var p = new (string Name, object? Value)[]
        {
            ("@tenant_id", tenantId.Value),
            ("@table_id", tableId.ToString()),
            ("@day", day),
            ("@application", application)
        };

        await using var cmd = Command(connection, tx, sql, p);
        var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return val is null or DBNull ? 0 : Convert.ToInt32(val, CultureInfo.InvariantCulture);
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
