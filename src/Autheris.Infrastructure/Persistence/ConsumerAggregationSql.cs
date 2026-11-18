namespace Autheris.Infrastructure.Persistence;

using System;

/// <summary>SQL dialect of the governance store a consumer aggregation statement runs against (Issue #286).</summary>
internal enum ConsumerAggregationDialect
{
    PostgreSql,
    SqlServer
}

/// <summary>
/// Issue #286, dialect specific upsert statements for <c>CONSUMER_APP_QUERYNAME_DAILY</c>.
/// Every value reaches the statement as a bound parameter, and every key (including <c>tenant_id</c> first)
/// takes part in the conflict target / MERGE match.
/// </summary>
internal sealed class ConsumerAggregationSql
{
    private static readonly ConsumerAggregationSql PostgreSqlSql = Build(ConsumerAggregationDialect.PostgreSql);
    private static readonly ConsumerAggregationSql SqlServerSql = Build(ConsumerAggregationDialect.SqlServer);

    private ConsumerAggregationSql(string queryNameUpsert)
    {
        QueryNameUpsert = queryNameUpsert;
    }

    public string QueryNameUpsert { get; }

    public static ConsumerAggregationSql For(ConsumerAggregationDialect dialect) => dialect switch
    {
        ConsumerAggregationDialect.PostgreSql => PostgreSqlSql,
        ConsumerAggregationDialect.SqlServer => SqlServerSql,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown consumer aggregation dialect.")
    };

    private static ConsumerAggregationSql Build(ConsumerAggregationDialect dialect) => dialect switch
    {
        ConsumerAggregationDialect.PostgreSql => BuildOnConflict(qualify: true),
        _ => BuildMerge()
    };

    // PostgreSQL reports an unqualified column in DO UPDATE SET as ambiguous.
    private static ConsumerAggregationSql BuildOnConflict(bool qualify)
    {
        string query = qualify ? "CONSUMER_APP_QUERYNAME_DAILY." : string.Empty;

        string queryUpsert = $@"
            INSERT INTO CONSUMER_APP_QUERYNAME_DAILY (tenant_id, table_id, day, application, query_name, calls)
            VALUES (@tenant_id, @table_id, @day, @application, @query_name, @calls)
            ON CONFLICT(tenant_id, table_id, day, application, query_name) DO UPDATE SET
                calls = {query}calls + excluded.calls;
        ";

        return new ConsumerAggregationSql(queryUpsert);
    }

    // HOLDLOCK serialises concurrent upserts of the same key (no PK violation, no lost update).
    private static ConsumerAggregationSql BuildMerge()
    {
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

        return new ConsumerAggregationSql(queryUpsert);
    }
}
