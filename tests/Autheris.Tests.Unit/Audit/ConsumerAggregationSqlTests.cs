using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>Issue #286: dialect specific upserts for the CONSUMER_APP_* tables (SEC-286-3, SEC-286-8) and the baseline-only runtime path.</summary>
public sealed class ConsumerAggregationSqlTests
{
    private static readonly string[] DailyKey = ["tenant_id", "table_id", "day", "application", "client_id", "version"];

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    [Trait("Category", "Security")]
    public void AllDialects_UseOnlyBoundParameters_AndTenantFirstInKey(string dialectName)
    {
        var sql = ConsumerAggregationSql.For(Enum.Parse<ConsumerAggregationDialect>(dialectName));

        foreach (string statement in new[] { sql.DailyUpsert, sql.ActorInsert, sql.QueryNameUpsert })
        {
            statement.ShouldNotContain("'", customMessage: "SEC-286-3: no string literal in the statement");
            statement.ShouldNotContain("{");
            statement.ShouldNotContain("}");
            statement.ShouldContain("@tenant_id");
            Regex.Matches(statement, "@\\w+").Select(m => m.Value).Distinct().ShouldAllBe(p => p.Length > 1);
        }

        // tenant_id is the first column of every INSERT column list.
        foreach (string statement in new[] { sql.DailyUpsert, sql.ActorInsert, sql.QueryNameUpsert })
        {
            Regex.Match(statement, @"INSERT(?: INTO CONSUMER_APP_\w+)?\s*\(\s*(\w+)").Groups[1].Value.ShouldBe("tenant_id");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Upsert_SqlServer_MatchOnClauseContainsEveryPrimaryKeyColumn()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.SqlServer);

        string dailyOn = OnClause(sql.DailyUpsert);
        foreach (string column in DailyKey)
        {
            dailyOn.ShouldContain($"t.{column} = s.{column}");
        }

        string actorOn = OnClause(sql.ActorInsert);
        foreach (string column in new[] { "tenant_id", "table_id", "day", "application", "actor_hash" })
        {
            actorOn.ShouldContain($"t.{column} = s.{column}");
        }

        string queryOn = OnClause(sql.QueryNameUpsert);
        foreach (string column in new[] { "tenant_id", "table_id", "day", "application", "query_name" })
        {
            queryOn.ShouldContain($"t.{column} = s.{column}");
        }
    }

    [Fact]
    public void Upsert_PostgreSql_QualifiesExistingRowColumns()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.PostgreSql);

        sql.DailyUpsert.ShouldContain("calls = CONSUMER_APP_DAILY.calls + excluded.calls");
        sql.DailyUpsert.ShouldContain("rej_other = CONSUMER_APP_DAILY.rej_other + excluded.rej_other");
        sql.QueryNameUpsert.ShouldContain("calls = CONSUMER_APP_QUERYNAME_DAILY.calls + excluded.calls");
        sql.ActorInsert.ShouldContain("DO NOTHING");
    }

    [Fact]
    public void Upsert_SqlServer_UsesMergeWithHoldlock()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.SqlServer);

        foreach (string statement in new[] { sql.DailyUpsert, sql.ActorInsert, sql.QueryNameUpsert })
        {
            statement.ShouldContain("MERGE dbo.CONSUMER_APP_");
            statement.ShouldContain("WITH (HOLDLOCK)");
            statement.ShouldNotContain("ON CONFLICT");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ActorUpsert_SqlServer_HasNoMatchedBranch()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.SqlServer);

        sql.ActorInsert.ShouldNotContain("WHEN MATCHED");
        sql.ActorInsert.ShouldContain("WHEN NOT MATCHED THEN INSERT");
    }

    [Fact]
    public void Upsert_Sqlite_IsUnchanged()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.Sqlite);

        sql.DailyUpsert.ShouldContain("calls = calls + excluded.calls");
        sql.DailyUpsert.ShouldNotContain("CONSUMER_APP_DAILY.calls");
        sql.QueryNameUpsert.ShouldContain("calls = calls + excluded.calls");
        sql.ActorInsert.ShouldContain("ON CONFLICT(tenant_id, table_id, day, application, actor_hash) DO NOTHING");
    }

    [Fact]
    public void For_UnknownDialect_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => ConsumerAggregationSql.For((ConsumerAggregationDialect)99));

    [Fact]
    public async Task RecordAndQuery_Sqlite_RoundTripsAfterBaselineOnly()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        new SqliteBaselineSchema(conn, null).Apply();
        var table = new TableIdentifier("sales", "dbo", "orders");
        var tenant = new TenantId("tenant-1");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, day, "powerbi", "c1", "1.0", 2, 20, 2, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, day, "powerbi", "c1", "1.0", 3, 30, 3, 1, 0, 0, 0);
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, day, "powerbi", new string('a', 64));
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, day, "powerbi", new string('a', 64));
        await ConsumerAggregationStore.RecordQueryNameAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, day, "powerbi", "GetOrders", 5);

        var rows = await ConsumerAggregationStore.QueryConsumerAppDailyAsync(conn, null, table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        row.Rejections.Access.ShouldBe(1);
        row.DistinctActors.ShouldBe(1);
        row.QueryNames.ShouldBe(["GetOrders"]);
    }

    [Fact]
    public async Task Store_OnDatabaseWithoutBaseline_FailsAndNeverCreatesTables()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var table = new TableIdentifier("sales", "dbo", "orders");

        await Should.ThrowAsync<SqliteException>(() => ConsumerAggregationStore.RecordQueryNameAsync(
            conn, ConsumerAggregationDialect.Sqlite, null, table, new TenantId("t"), "2026-10-23", "app", "q", 1));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'CONSUMER_APP_%'";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(0);
    }

    private static string OnClause(string merge)
    {
        int on = merge.IndexOf("ON t.", StringComparison.Ordinal);
        int when = merge.IndexOf("WHEN", on, StringComparison.Ordinal);
        return merge[on..when];
    }
}
