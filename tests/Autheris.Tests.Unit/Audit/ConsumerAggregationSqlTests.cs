using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>Issue #286: dialect specific upserts for CONSUMER_APP_QUERYNAME_DAILY (SEC-286-3, SEC-286-8) and the baseline-only runtime path.</summary>
public sealed class ConsumerAggregationSqlTests
{
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    [Trait("Category", "Security")]
    public void AllDialects_UseOnlyBoundParameters_AndTenantFirstInKey(string dialectName)
    {
        var sql = ConsumerAggregationSql.For(Enum.Parse<ConsumerAggregationDialect>(dialectName));

        string statement = sql.QueryNameUpsert;
        statement.ShouldNotContain("'", customMessage: "SEC-286-3: no string literal in the statement");
        statement.ShouldNotContain("{");
        statement.ShouldNotContain("}");
        statement.ShouldContain("@tenant_id");
        Regex.Matches(statement, "@\\w+").Select(m => m.Value).Distinct().ShouldAllBe(p => p.Length > 1);

        // tenant_id is the first column of every INSERT column list.
        Regex.Match(statement, @"INSERT(?: INTO CONSUMER_APP_\w+)?\s*\(\s*(\w+)").Groups[1].Value.ShouldBe("tenant_id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Upsert_SqlServer_MatchOnClauseContainsEveryPrimaryKeyColumn()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.SqlServer);

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

        sql.QueryNameUpsert.ShouldContain("calls = CONSUMER_APP_QUERYNAME_DAILY.calls + excluded.calls");
    }

    [Fact]
    public void Upsert_SqlServer_UsesMergeWithHoldlock()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.SqlServer);

        sql.QueryNameUpsert.ShouldContain("MERGE dbo.CONSUMER_APP_");
        sql.QueryNameUpsert.ShouldContain("WITH (HOLDLOCK)");
        sql.QueryNameUpsert.ShouldNotContain("ON CONFLICT");
    }

    [Fact]
    public void Upsert_Sqlite_IsUnchanged()
    {
        var sql = ConsumerAggregationSql.For(ConsumerAggregationDialect.Sqlite);

        sql.QueryNameUpsert.ShouldContain("calls = calls + excluded.calls");
        sql.QueryNameUpsert.ShouldNotContain("CONSUMER_APP_QUERYNAME_DAILY.calls");
    }

    [Fact]
    public void For_UnknownDialect_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => ConsumerAggregationSql.For((ConsumerAggregationDialect)99));

    private static string OnClause(string merge)
    {
        int on = merge.IndexOf("ON t.", StringComparison.Ordinal);
        int when = merge.IndexOf("WHEN", on, StringComparison.Ordinal);
        return merge[on..when];
    }
}
