namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Linq;
using System.Reflection;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

/// <summary>Issue #610 (WP-OI-CAT-6): TABLE_COLUMNS carries a registration ordinal; reads order by it and inserts append after the table's current maximum.</summary>
public sealed class TableColumnOrdinalSqlTests
{
    private static string Sql(string name) =>
        (string)typeof(GovernanceRepository).GetNestedType("CatalogSql", BindingFlags.NonPublic)!
            .GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    [Theory]
    [InlineData("SelectTableColumnsSqlPg")]
    [InlineData("SelectTableColumnsSqlSs")]
    public void SingleTableRead_OrdersByOrdinalThenName(string field) =>
        Sql(field).ShouldEndWith("ORDER BY c.ordinal, c.column_name");

    [Theory]
    [InlineData("SelectActiveBatchTableColumnsSqlPg")]
    [InlineData("SelectActiveBatchTableColumnsSqlSs")]
    public void BatchRead_OrdersByTableThenOrdinalThenName(string field) =>
        Sql(field).ShouldEndWith("ORDER BY c.table_id, c.ordinal, c.column_name");

    [Theory]
    [InlineData("SelectTableColumnsSqlPg")]
    [InlineData("SelectActiveBatchTableColumnsSqlPg")]
    public void Reads_NoLongerDependOnHeapOrder(string field) =>
        Sql(field).ShouldNotContain("ctid");

    [Theory]
    [InlineData("InsertColumnSqlPg")]
    [InlineData("InsertColumnSqlSs")]
    public void Insert_AppendsAfterTheTablesCurrentMaximumOrdinal(string field)
    {
        var sql = Sql(field);
        sql.ShouldContain("ordinal");
        sql.ShouldContain("COALESCE(MAX(ordinal), 0) + 1");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0010_IsEmbedded_AndAddsOrdinal(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 10);

        step.Name.ShouldBe("table_column_ordinal");
        step.Sql.ShouldContain("ordinal");
        step.Sql.ShouldContain(engine == "postgresql" ? "ORDER BY ctid" : "ORDER BY column_name");
    }
}
