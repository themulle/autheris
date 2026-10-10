namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-A7 execution evidence on in-process DuckDB (tenant column <c>COLLATE NOCASE</c>, so a plain equality would leak the case
/// variant). The scenarios are the shared <see cref="AstCompilerDmlContract"/>; every test owns one in-memory database.
/// </summary>
public sealed class AstCompilerDuckDbDmlExecutionTests : AstCompilerDmlContract, IAsyncLifetime, IDisposable
{
    private readonly DuckDBConnection _conn = new("DataSource=:memory:");
    private readonly DuckDbCompiledSqlBinder _binder = new();

    protected override TargetSqlDialect Dialect => TargetSqlDialect.DuckDb;
    protected override TableIdentity OrdersId => new("main", "Orders");
    protected override TableIdentity EntitlementsId => new("main", "Entitlements");
    protected override string Canon(string logical) => logical;
    protected override string Quote(string identifier) => "\"" + identifier + "\"";
    protected override string RawTable(string logical) => $"\"main\".\"{logical}\"";
    protected override string UserTable(string logical) => logical;
    protected override bool Available() => true;

    public async Task InitializeAsync()
    {
        _conn.Open();
        await ExecRaw("""
            CREATE TABLE "Orders" (
                "Id" INTEGER PRIMARY KEY,
                "TenantId" VARCHAR COLLATE NOCASE NOT NULL,
                "Region" VARCHAR NOT NULL,
                "Status" VARCHAR NOT NULL,
                "Amount" DECIMAL(18,2) NOT NULL,
                "Email" VARCHAR,
                "Due" DATE);
            CREATE TABLE "Entitlements" ("Id" INTEGER PRIMARY KEY, "TenantId" VARCHAR COLLATE NOCASE NOT NULL, "OrderId" INTEGER NOT NULL);
            """);
        foreach (var seed in SeedStatements()) await ExecRaw(seed);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _conn.Dispose();

    private Task ExecRaw(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    protected override InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(OrdersId, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "VARCHAR"), new CatalogColumn("Region", "VARCHAR"),
            new CatalogColumn("Status", "VARCHAR"), new CatalogColumn("Amount", "DECIMAL(18,2)"), new CatalogColumn("Email", "VARCHAR"), new CatalogColumn("Due", "DATE")),
            "TenantId", 1),
        new TableCatalogEntry(EntitlementsId, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "VARCHAR"), new CatalogColumn("OrderId", "INTEGER")),
            "TenantId", 1)
    }, "main");

    protected override Task<int> ExecuteAsync(CompiledSql compiled)
    {
        using var cmd = _conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        return Task.FromResult(cmd.ExecuteNonQuery());
    }

    protected override Task<int> ExecuteInTransactionAsync(CompiledSql compiled) =>
        RunCheckedAsync(_conn, false, cmd => _binder.Bind(cmd, compiled, new Dictionary<string, object?>()), compiled, System.Data.IsolationLevel.Unspecified);

    protected override Task<List<object?[]>> QueryAsync(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<object?[]>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return Task.FromResult(rows);
    }

    [Fact]
    public async Task TheTenantColumnIsReallyCaseInsensitive_SoThePlainEqualityWouldHaveLeaked()
    {
        using var probe = _conn.CreateCommand();
        probe.CommandText = "SELECT count(*) FROM \"Orders\" WHERE \"TenantId\" = 'acme'";
        Convert.ToInt32(probe.ExecuteScalar()).ShouldBe(5);
        (await ExecAsync("UPDATE orders SET status = 'z' WHERE id > 0", "acme")).ShouldBe(3);
    }
}
