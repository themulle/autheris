namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-A7 execution evidence on a real SQL Server (Testcontainers or AUTHERIS_TEST_MSSQL), the case-insensitive column collation
/// <c>SQL_Latin1_General_CP1_CI_AS</c> included. The scenarios are the shared <see cref="AstCompilerDmlContract"/>; every test
/// runs in its own schema, so the data mutation of one test is invisible to the others.
/// </summary>
public sealed class AstCompilerSqlServerDmlExecutionTests : AstCompilerDmlContract, IClassFixture<AstCompilerSqlServerFixture>, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _db;
    private readonly string _schema = "dml_" + Guid.NewGuid().ToString("N")[..12];
    private readonly SqlServerCompiledSqlBinder _binder = new();

    public AstCompilerSqlServerDmlExecutionTests(AstCompilerSqlServerFixture fixture) => _db = fixture.Db!;

    protected override TargetSqlDialect Dialect => TargetSqlDialect.SqlServer;
    protected override TableIdentity OrdersId => new(_schema, "Orders");
    protected override TableIdentity EntitlementsId => new(_schema, "Entitlements");
    protected override string Canon(string logical) => logical;
    protected override string Quote(string identifier) => "[" + identifier + "]";
    protected override string RawTable(string logical) => $"[{_schema}].[{logical}]";
    protected override string UserTable(string logical) => $"{_schema}.{logical}";

    protected override bool Available()
    {
        if (_db.IsAvailable) return true;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new Xunit.Sdk.XunitException("The SQL Server container is unavailable on CI; the DML execution evidence cannot be skipped.");
        }

        return false;
    }

    public async Task InitializeAsync()
    {
        if (!Available()) return;
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA [{_schema}];";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = $"""
            CREATE TABLE [{_schema}].Orders (
                Id int NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                Region nvarchar(20) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
                Status nvarchar(20) NOT NULL,
                Amount decimal(18,2) NOT NULL,
                Email nvarchar(200) NULL,
                Due date NULL);
            CREATE TABLE [{_schema}].Entitlements (
                Id int NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                OrderId int NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
        foreach (var seed in SeedStatements())
        {
            cmd.CommandText = seed;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected override InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(OrdersId, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Region", "nvarchar(20)"),
            new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "nvarchar(200)"), new CatalogColumn("Due", "date")),
            "TenantId", 1),
        new TableCatalogEntry(EntitlementsId, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")),
            "TenantId", 1)
    }, _schema);

    protected override async Task<int> ExecuteAsync(CompiledSql compiled)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        return await cmd.ExecuteNonQueryAsync();
    }

    protected override Task<int> ExecuteInTransactionAsync(CompiledSql compiled) =>
        RunCheckedAsync(new SqlConnection(_db.ConnectionString), true, _binder, compiled);

    protected override async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    // ---- SQL Server specifics ----

    [Fact]
    public async Task Merge_IsEmittedWithExactlyOneTrailingSemicolon_AndExecutes()
    {
        if (!Available()) return;
        var compiled = Engine.Compile($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'm'".AsMemory(), Request("acme"), default);
        compiled.Sql.ShouldEndWith(";");
        compiled.Sql.Count(c => c == ';').ShouldBe(1);
        (await ExecuteAsync(compiled)).ShouldBe(2);
    }

    [Fact]
    public async Task Insert_DuplicateKey_RawDriverTextCarriesTheKey_TheTypedErrorDoesNot()
    {
        if (!Available()) return;
        var raw = await Should.ThrowAsync<SqlException>(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (5, 'acme', 'EU', 'x', 1)", "acme"));
        raw.Message.ShouldContain("5");
        raw.Message.ShouldContain(_schema);   // the schema and constraint names are in the driver text only
        var mapped = DmlErrorSanitizer.TryMap(TargetSqlDialect.SqlServer, raw)!;
        mapped.ToString().ShouldNotContain(_schema);
        mapped.ToString().ShouldNotContain("PK__");
    }
}
