namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-A7 execution evidence on a real PostgreSQL 16 (Testcontainers). The tenant column is <c>citext</c>, so a plain equality would
/// match the case variant. The scenarios are the shared <see cref="AstCompilerDmlContract"/>; every test owns one schema.
/// </summary>
public sealed class AstCompilerPostgreSqlDmlExecutionTests : AstCompilerDmlContract, IClassFixture<AstCompilerPostgreSqlFixture>, IAsyncLifetime
{
    private readonly AstCompilerPostgreSqlFixture _fx;
    private readonly string _schema = "dml_" + Guid.NewGuid().ToString("N")[..12];
    private readonly PostgreSqlCompiledSqlBinder _binder = new();

    public AstCompilerPostgreSqlDmlExecutionTests(AstCompilerPostgreSqlFixture fixture) => _fx = fixture;

    protected override TargetSqlDialect Dialect => TargetSqlDialect.PostgreSql;
    protected override TableIdentity OrdersId => new(_schema, "orders");
    protected override TableIdentity EntitlementsId => new(_schema, "entitlements");

    // PostgreSQL folds unquoted names to lower case, so the catalog spelling is lower case.
    protected override string Canon(string logical) => logical.ToLowerInvariant();
    protected override string Quote(string identifier) => "\"" + identifier + "\"";
    protected override string RawTable(string logical) => $"\"{_schema}\".\"{logical.ToLowerInvariant()}\"";
    protected override string UserTable(string logical) => $"{_schema}.{logical.ToLowerInvariant()}";
    protected override bool Available() => true;
    protected override string ReadExpr(string logical) => logical == "TenantId" ? "CAST(\"tenantid\" AS text)" : Quote(Canon(logical));

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA {_schema};
            CREATE TABLE {_schema}.orders (
                id integer PRIMARY KEY,
                tenantid public.citext NOT NULL,
                region text NOT NULL,
                status text NOT NULL,
                amount numeric(18,2) NOT NULL,
                email text,
                due date);
            CREATE TABLE {_schema}.entitlements (id integer PRIMARY KEY, tenantid public.citext NOT NULL, orderid integer NOT NULL);
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
            new CatalogColumn("id", "integer"), new CatalogColumn("tenantid", "citext"), new CatalogColumn("region", "text"),
            new CatalogColumn("status", "text"), new CatalogColumn("amount", "numeric(18,2)"), new CatalogColumn("email", "text"), new CatalogColumn("due", "date")),
            "tenantid", 1),
        new TableCatalogEntry(EntitlementsId, ImmutableArray.Create(
            new CatalogColumn("id", "integer"), new CatalogColumn("tenantid", "citext"), new CatalogColumn("orderid", "integer")),
            "tenantid", 1)
    }, _schema);

    protected override async Task<int> ExecuteAsync(CompiledSql compiled)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        return await cmd.ExecuteNonQueryAsync();
    }

    protected override Task<int> ExecuteInTransactionAsync(CompiledSql compiled) =>
        RunCheckedAsync(new NpgsqlConnection(_fx.ConnectionString), true, cmd => _binder.Bind(cmd, compiled, new Dictionary<string, object?>()), compiled);

    protected override async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
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
        var probe = await QueryAsync($"SELECT count(*) FROM {RawTable("Orders")} WHERE tenantid = 'acme'");
        Convert.ToInt32(probe[0][0]).ShouldBe(5);
        (await ExecAsync($"UPDATE {O} SET status = 'z' WHERE id > 0", "acme")).ShouldBe(3);
    }

    [Fact]
    public async Task HostileSearchPath_PgTempDecoy_DoesNotRedirectADmlTarget()
    {
        // The emitted target is schema-qualified (INV-11), so a pg_temp decoy table of the same name is never written.
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = $"SET search_path = pg_temp, public; CREATE TEMP TABLE orders (id integer, tenantid text, region text, status text, amount numeric, email text); INSERT INTO pg_temp.orders VALUES (1, 'acme', 'EU', 'decoy', 1, NULL);";
            await setup.ExecuteNonQueryAsync();
        }

        var compiled = Engine.Compile($"UPDATE {O} SET status = 'real' WHERE id > 0".AsMemory(), Request("acme"), default);
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        (await cmd.ExecuteNonQueryAsync()).ShouldBe(3);
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT status FROM pg_temp.orders";
        ((string)(await check.ExecuteScalarAsync())!).ShouldBe("decoy");
    }
}
