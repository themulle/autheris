using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>SEC-94-08c: table and index names from the snapshot are bound as parameters, never interpolated.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceSchemaCatalogReaderTests
{
    [Fact]
    public async Task ShapeTableName_IsParameterBound()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE T1 (id TEXT PRIMARY KEY, v TEXT); CREATE UNIQUE INDEX ux_t1_v ON T1 (v);";
            await create.ExecuteNonQueryAsync();
        }

        var hostile = new[] { "x'; DROP TABLE T1;--", "T1' OR '1'='1", "T1;", "\"T1\"", "T1" };

        var shape = await new SqliteSchemaCatalogReader().ReadAsync(connection, hostile, CancellationToken.None);

        shape.Tables.Select(t => t.Name).ShouldBe(["T1"]);
        shape.Tables[0].PrimaryKey.ShouldBe(["id"]);
        shape.Tables[0].Indexes.ShouldHaveSingleItem().Columns.ShouldBe(["v"]);
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'T1'";
        Convert.ToInt64(await check.ExecuteScalarAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task MissingTable_IsLeftOut_AndTypesMapToFamilies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE T2 (a INTEGER, b TEXT, c REAL, d BLOB, e NUMERIC);";
            await create.ExecuteNonQueryAsync();
        }

        var shape = await new SqliteSchemaCatalogReader().ReadAsync(connection, ["T2", "NOPE"], CancellationToken.None);

        shape.Tables.ShouldHaveSingleItem().Columns.Select(c => c.Family).ShouldBe(["integer", "string", "float", "binary", "decimal"]);
    }
}
