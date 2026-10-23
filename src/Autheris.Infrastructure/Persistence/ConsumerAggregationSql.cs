namespace Autheris.Infrastructure.Persistence;

using System;

/// <summary>SQL dialect of the governance store a consumer aggregation statement runs against (Issue #286).</summary>
internal enum ConsumerAggregationDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

/// <summary>
/// Issue #286, SEC-286-3: dialect specific upsert statements for the CONSUMER_APP_* tables. The builder takes only the
/// dialect, never caller data: every value reaches the statement as a bound parameter, and every key (including
/// <c>tenant_id</c> first) takes part in the conflict target / MERGE match.
/// </summary>
internal sealed class ConsumerAggregationSql
{
    private static readonly ConsumerAggregationSql SqliteSql = Build(ConsumerAggregationDialect.Sqlite);
    private static readonly ConsumerAggregationSql PostgreSqlSql = Build(ConsumerAggregationDialect.PostgreSql);
    private static readonly ConsumerAggregationSql SqlServerSql = Build(ConsumerAggregationDialect.SqlServer);

    private ConsumerAggregationSql(string dailyUpsert, string actorInsert, string queryNameUpsert)
    {
        DailyUpsert = dailyUpsert;
        ActorInsert = actorInsert;
        QueryNameUpsert = queryNameUpsert;
    }

    public string DailyUpsert { get; }

    public string ActorInsert { get; }

    public string QueryNameUpsert { get; }

    public static ConsumerAggregationSql For(ConsumerAggregationDialect dialect) => dialect switch
    {
        ConsumerAggregationDialect.Sqlite => SqliteSql,
        ConsumerAggregationDialect.PostgreSql => PostgreSqlSql,
        ConsumerAggregationDialect.SqlServer => SqlServerSql,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown consumer aggregation dialect.")
    };

    private static ConsumerAggregationSql Build(ConsumerAggregationDialect dialect) => dialect switch
    {
        ConsumerAggregationDialect.Sqlite => BuildOnConflict(qualify: false),
        ConsumerAggregationDialect.PostgreSql => BuildOnConflict(qualify: true),
        _ => BuildMerge()
    };

    // SQLite resolves an unqualified column in DO UPDATE SET to the existing row; PostgreSQL reports it as ambiguous.
    private static ConsumerAggregationSql BuildOnConflict(bool qualify)
    {
        string daily = qualify ? "CONSUMER_APP_DAILY." : string.Empty;
        string query = qualify ? "CONSUMER_APP_QUERYNAME_DAILY." : string.Empty;

        string dailyUpsert = $@"
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
                calls = {daily}calls + excluded.calls,
                rows_returned = {daily}rows_returned + excluded.rows_returned,
                rows_measured_calls = {daily}rows_measured_calls + excluded.rows_measured_calls,
                rej_access = {daily}rej_access + excluded.rej_access,
                rej_rate_limit = {daily}rej_rate_limit + excluded.rej_rate_limit,
                rej_cost = {daily}rej_cost + excluded.rej_cost,
                rej_other = {daily}rej_other + excluded.rej_other;
        ";

        const string actorInsert = @"
            INSERT INTO CONSUMER_APP_ACTOR_DAILY (tenant_id, table_id, day, application, actor_hash)
            VALUES (@tenant_id, @table_id, @day, @application, @actor_hash)
            ON CONFLICT(tenant_id, table_id, day, application, actor_hash) DO NOTHING;
        ";

        string queryUpsert = $@"
            INSERT INTO CONSUMER_APP_QUERYNAME_DAILY (tenant_id, table_id, day, application, query_name, calls)
            VALUES (@tenant_id, @table_id, @day, @application, @query_name, @calls)
            ON CONFLICT(tenant_id, table_id, day, application, query_name) DO UPDATE SET
                calls = {query}calls + excluded.calls;
        ";

        return new ConsumerAggregationSql(dailyUpsert, actorInsert, queryUpsert);
    }

    // HOLDLOCK serialises concurrent upserts of the same key (no PK violation, no lost update).
    private static ConsumerAggregationSql BuildMerge()
    {
        const string dailyUpsert = @"
            MERGE dbo.CONSUMER_APP_DAILY WITH (HOLDLOCK) AS t
            USING (SELECT @tenant_id AS tenant_id, @table_id AS table_id, @day AS day, @application AS application,
                          @client_id AS client_id, @version AS version,
                          @calls AS calls, @rows_returned AS rows_returned, @rows_measured_calls AS rows_measured_calls,
                          @rej_access AS rej_access, @rej_rate_limit AS rej_rate_limit, @rej_cost AS rej_cost,
                          @rej_other AS rej_other) AS s
            ON t.tenant_id = s.tenant_id AND t.table_id = s.table_id AND t.day = s.day
               AND t.application = s.application AND t.client_id = s.client_id AND t.version = s.version
            WHEN MATCHED THEN UPDATE SET
                t.calls = t.calls + s.calls,
                t.rows_returned = t.rows_returned + s.rows_returned,
                t.rows_measured_calls = t.rows_measured_calls + s.rows_measured_calls,
                t.rej_access = t.rej_access + s.rej_access,
                t.rej_rate_limit = t.rej_rate_limit + s.rej_rate_limit,
                t.rej_cost = t.rej_cost + s.rej_cost,
                t.rej_other = t.rej_other + s.rej_other
            WHEN NOT MATCHED THEN INSERT (
                tenant_id, table_id, day, application, client_id, version,
                calls, rows_returned, rows_measured_calls, rej_access, rej_rate_limit, rej_cost, rej_other)
            VALUES (
                s.tenant_id, s.table_id, s.day, s.application, s.client_id, s.version,
                s.calls, s.rows_returned, s.rows_measured_calls, s.rej_access, s.rej_rate_limit, s.rej_cost, s.rej_other);
        ";

        // SEC-286-8: insert-if-absent only (no WHEN MATCHED branch).
        const string actorInsert = @"
            MERGE dbo.CONSUMER_APP_ACTOR_DAILY WITH (HOLDLOCK) AS t
            USING (SELECT @tenant_id AS tenant_id, @table_id AS table_id, @day AS day, @application AS application,
                          CAST(@actor_hash AS CHAR(64)) AS actor_hash) AS s
            ON t.tenant_id = s.tenant_id AND t.table_id = s.table_id AND t.day = s.day
               AND t.application = s.application AND t.actor_hash = s.actor_hash
            WHEN NOT MATCHED THEN INSERT (tenant_id, table_id, day, application, actor_hash)
            VALUES (s.tenant_id, s.table_id, s.day, s.application, s.actor_hash);
        ";

        const string queryUpsert = @"
            MERGE dbo.CONSUMER_APP_QUERYNAME_DAILY WITH (HOLDLOCK) AS t
            USING (SELECT @tenant_id AS tenant_id, @table_id AS table_id, @day AS day, @application AS application,
                          @query_name AS query_name, @calls AS calls) AS s
            ON t.tenant_id = s.tenant_id AND t.table_id = s.table_id AND t.day = s.day
               AND t.application = s.application AND t.query_name = s.query_name
            WHEN MATCHED THEN UPDATE SET t.calls = t.calls + s.calls
            WHEN NOT MATCHED THEN INSERT (tenant_id, table_id, day, application, query_name, calls)
            VALUES (s.tenant_id, s.table_id, s.day, s.application, s.query_name, s.calls);
        ";

        return new ConsumerAggregationSql(dailyUpsert, actorInsert, queryUpsert);
    }
}
