namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0h execution evidence for SQLite DML on in-process SQLite (tenant column <c>COLLATE NOCASE</c>, so a plain equality would
/// leak the case variant). The scenarios are the shared <see cref="AstCompilerDmlContract"/>; SQLite has no MERGE. Every test owns
/// one shared-cache in-memory database (a keep-alive connection plus the session connections of the X0k scenarios).
/// </summary>
public sealed class AstCompilerSqliteDmlExecutionTests : AstCompilerDmlContract, IAsyncLifetime, IDisposable
{
    private readonly string _connectionString = $"Data Source=file:ast_sqlite_dml_{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection _conn = null!;

    protected override TargetSqlDialect Dialect => TargetSqlDialect.Sqlite;
    protected override TableIdentity OrdersId => new("main", "Orders");
    protected override TableIdentity EntitlementsId => new("main", "Entitlements");
    protected override string Canon(string logical) => logical;
    protected override string Quote(string identifier) => "\"" + identifier + "\"";
    protected override string RawTable(string logical) => $"\"main\".\"{logical}\"";
    protected override string UserTable(string logical) => logical;
    protected override bool Available() => true;
    protected override bool SupportsMerge => false;
    protected override bool SupportsMergeDelete => false;
    protected override bool StorageAltersValues => false;   // SQLite stores what was written: no decimal rounding, no date truncation

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection(_connectionString);
        await _conn.OpenAsync();
        await ExecRaw("""
            CREATE TABLE "Orders" (
                "Id" INTEGER PRIMARY KEY,
                "TenantId" TEXT COLLATE NOCASE NOT NULL,
                "Region" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Amount" NUMERIC NOT NULL,
                "Email" TEXT,
                "Due" DATE);
            CREATE TABLE "Entitlements" ("Id" INTEGER PRIMARY KEY, "TenantId" TEXT COLLATE NOCASE NOT NULL, "OrderId" INTEGER NOT NULL);
            """);
        foreach (var seed in SeedStatements()) await ExecRaw(seed);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _conn.Dispose();

    private async Task ExecRaw(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected override InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(OrdersId, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "TEXT"), new CatalogColumn("Region", "TEXT"),
            new CatalogColumn("Status", "TEXT"), new CatalogColumn("Amount", "NUMERIC"), new CatalogColumn("Email", "TEXT"), new CatalogColumn("Due", "DATE")),
            "TenantId", 1),
        new TableCatalogEntry(EntitlementsId, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "TEXT"), new CatalogColumn("OrderId", "INTEGER")),
            "TenantId", 1)
    }, "main");

    protected override async Task<int> ExecuteAsync(CompiledSql compiled)
    {
        await using var cmd = _conn.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(cmd, compiled, new Dictionary<string, object?>());
        return await cmd.ExecuteNonQueryAsync();
    }

    protected override Task<int> ExecuteInTransactionAsync(CompiledSql compiled) =>
        RunCheckedAsync(_conn, false, compiled, System.Data.IsolationLevel.Unspecified);

    protected override async Task<DbConnection?> OpenSessionConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    protected override async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    [Fact]
    public async Task TheTenantColumnIsReallyCaseInsensitive_SoThePlainEqualityWouldHaveLeaked()
    {
        await using var probe = _conn.CreateCommand();
        probe.CommandText = "SELECT count(*) FROM \"Orders\" WHERE \"TenantId\" = 'acme'";
        Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(5);
        (await ExecAsync("UPDATE orders SET status = 'z' WHERE id > 0", "acme")).ShouldBe(3);
    }

    [Fact]
    public async Task CheckedDml_RunsUnderTheGuard_InTheSessionTransaction_AfterAnInterruptedStatement()
    {
        // An interrupted statement of the session must not poison the session transaction, and the checked executor (X0k) runs
        // the compiled DML inside it under the guard and commits it.
        var compiled = Engine.Compile($"UPDATE {O} SET status = 'late' WHERE id > 0".AsMemory(), Request("acme"), default);
        await using var connection = (SqliteConnection)(await OpenSessionConnectionAsync())!;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var slow = connection.CreateCommand())
            {
                slow.Transaction = (SqliteTransaction)transaction;
                slow.CommandText = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 60000000) SELECT count(*) FROM c";
                var clock = Stopwatch.StartNew();
                await Should.ThrowAsync<TimeoutException>(() => SqliteExecutionGuard.RunAsync(
                    connection, TimeSpan.FromMilliseconds(200), async () => await slow.ExecuteScalarAsync(), CancellationToken.None));
                clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1200));
            }

            // The transaction is still usable and the checked DML runs inside it under a guard with a generous budget.
            int affected = await SqliteExecutionGuard.RunAsync(connection, TimeSpan.FromSeconds(10),
                () => CompiledSqlBinders.CheckedExecutorFor(Dialect).ExecuteInTransactionAsync(transaction, compiled,
                    new Dictionary<string, object?>(), new CheckedDmlLimits(30, 0)), CancellationToken.None);
            affected.ShouldBe(3);
        }

        Convert.ToInt32((await QueryAsync($"SELECT count(*) FROM {RawTable("Orders")} WHERE \"Status\" = 'late'"))[0][0]).ShouldBe(3);
    }
}
