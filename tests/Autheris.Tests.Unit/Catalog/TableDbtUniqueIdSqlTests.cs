namespace Autheris.Tests.Unit.Catalog;

using System.Linq;
using System.Reflection;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

/// <summary>Issue #666: TABLES.dbt_unique_id is read with the entry, written on insert and kept when an update carries none.</summary>
public sealed class TableDbtUniqueIdSqlTests
{
    private static string Sql(string name) =>
        (string)typeof(GovernanceRepository).GetNestedType("CatalogSql", BindingFlags.NonPublic)!
            .GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    [Theory]
    [InlineData("SelectTableEntitySql")]
    [InlineData("SelectActiveTableEntitiesSql")]
    public void EntityReads_SelectTheUniqueId(string field) => Sql(field).ShouldContain("dbt_unique_id");

    [Fact]
    public void UpsertPg_KeepsStoredUniqueId_WhenIncomingIsNull() =>
        Sql("UpsertTableSqlPg").ShouldContain("dbt_unique_id = COALESCE(EXCLUDED.dbt_unique_id, TABLES.dbt_unique_id)");

    [Fact]
    public void UpsertSs_KeepsStoredUniqueId_WhenIncomingIsNull() =>
        Sql("UpsertTableSqlSs").ShouldContain("dbt_unique_id = COALESCE(@dbtUniqueId, t.dbt_unique_id)");

    [Theory]
    [InlineData("UpsertTableSqlPg")]
    [InlineData("UpsertTableSqlSs")]
    public void Upsert_InsertsTheUniqueId(string field)
    {
        var sql = Sql(field);
        sql.ShouldContain("non_personal, dbt_unique_id)");
        sql.ShouldContain("@dbtUniqueId");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0012_IsEmbedded_AddsANullableColumnWithoutBackfill(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 12);

        step.Name.ShouldBe("dbt_unique_id");
        step.Sql.ShouldContain("dbt_unique_id");
        step.Sql.ShouldContain("NULL");
        step.Sql.ShouldNotContain("UPDATE ");
        step.Sql.ShouldNotContain("NOT NULL");
    }
}
