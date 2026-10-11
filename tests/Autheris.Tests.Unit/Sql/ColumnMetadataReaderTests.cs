using Autheris.Application.Sql.Catalog;
using Autheris.Infrastructure.Persistence.Catalog;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>WP-X0c: the in-process readers (SQLite, DuckDB). The container-backed readers are covered by the integration tests.</summary>
public sealed class ColumnMetadataReaderTests
{
    private static async Task<List<DbColumnMetadata>> Collect(IColumnMetadataReader reader, System.Data.Common.DbConnection c, TableIdentity t)
    {
        var list = new List<DbColumnMetadata>();
        await foreach (var column in reader.ReadAsync(c, t, CancellationToken.None)) list.Add(column);
        return list;
    }

    [Fact]
    public async Task Sqlite_DeclaredType_NullCollation()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, Name VARCHAR(64) COLLATE NOCASE, amount NUMERIC(10,2), note TEXT, untyped)";
            await cmd.ExecuteNonQueryAsync();
        }

        var columns = await Collect(new SqliteColumnMetadataReader(), conn, new TableIdentity("main", "t"));

        columns.Select(c => (c.Name, c.NativeType, c.Collation)).ToArray().ShouldBe(new (string, string, string?)[]
        {
            ("id", "INTEGER", null), ("Name", "VARCHAR(64)", null), ("amount", "NUMERIC(10,2)", null), ("note", "TEXT", null), ("untyped", "", null)
        });
    }

    [Fact]
    public async Task Sqlite_UnknownTable_ReturnsNoColumns()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        (await Collect(new SqliteColumnMetadataReader(), conn, new TableIdentity("main", "nope"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Sqlite_HostileTableName_IsBoundNotConcatenated()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t (id INTEGER)";
            await cmd.ExecuteNonQueryAsync();
        }

        (await Collect(new SqliteColumnMetadataReader(), conn, new TableIdentity("main", "t') OR 1=1 --"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task DuckDb_ReadsFullType_CollationIsNullBecauseDuckDbDoesNotExposeIt()
    {
        await using var conn = new DuckDBConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE orders (id INTEGER, tenant_id VARCHAR COLLATE NOCASE, amount DECIMAL(18,3), name VARCHAR)";
            await cmd.ExecuteNonQueryAsync();
        }

        var columns = await Collect(new DuckDbColumnMetadataReader(), conn, new TableIdentity("main", "orders"));

        columns.Select(c => (c.Name, c.NativeType, c.Collation)).ToArray().ShouldBe(new (string, string, string?)[]
        {
            ("id", "integer", null), ("tenant_id", "varchar", null), ("amount", "decimal(18,3)", null), ("name", "varchar", null)
        });
    }

    [Fact]
    public async Task DuckDb_UnknownTable_ReturnsNoColumns()
    {
        await using var conn = new DuckDBConnection("DataSource=:memory:");
        await conn.OpenAsync();

        (await Collect(new DuckDbColumnMetadataReader(), conn, new TableIdentity("main", "nope"))).ShouldBeEmpty();
    }
}
