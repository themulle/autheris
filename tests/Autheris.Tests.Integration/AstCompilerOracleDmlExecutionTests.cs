namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-A7 execution evidence on Oracle Free 23ai (image pinned by digest, see <see cref="AstCompilerOracleFixture"/>) through the real
/// gateway path: <see cref="SqlConnectionFactory"/> (NLS pinned on every rental, although a logon trigger makes every raw session
/// case-insensitive), <see cref="DbSessionContextInitializer"/> and <see cref="OracleCompiledSqlBinder"/> with <c>BindByName</c>.
/// DML runs in an explicit transaction. Every test owns two tables. The shared <see cref="AstCompilerDmlContract"/> runs with the
/// Oracle MERGE form (one UPDATE and one INSERT clause, no stand-alone DELETE).
/// </summary>
public sealed class AstCompilerOracleDmlExecutionTests : AstCompilerDmlContract, IClassFixture<AstCompilerOracleFixture>, IAsyncLifetime
{
    private readonly AstCompilerOracleFixture _fx;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    private readonly OracleCompiledSqlBinder _binder = new();
    private readonly SqlConnectionFactory _factory = new(new DevelopmentEnvironment());
    private readonly DbSessionContextInitializer _sessions = new();

    public AstCompilerOracleDmlExecutionTests(AstCompilerOracleFixture fixture) => _fx = fixture;

    protected override TargetSqlDialect Dialect => TargetSqlDialect.Oracle;
    protected override bool SupportsMergeDelete => false;
    protected override TableIdentity OrdersId => new(AstCompilerOracleFixture.AppUser, "ORD_" + _suffix);
    protected override TableIdentity EntitlementsId => new(AstCompilerOracleFixture.AppUser, "ENT_" + _suffix);
    protected override string Canon(string logical) => logical.ToUpperInvariant();
    protected override string Quote(string identifier) => "\"" + identifier + "\"";
    protected override string RawTable(string logical) => $"\"{AstCompilerOracleFixture.AppUser}\".\"{Physical(logical)}\"";
    protected override string UserTable(string logical) => $"{AstCompilerOracleFixture.AppUser.ToLowerInvariant()}.{Physical(logical).ToLowerInvariant()}";
    protected override bool Available() => true;

    private string Physical(string logical) => logical == "Orders" ? "ORD_" + _suffix : "ENT_" + _suffix;

    private DataSourceConnectionOptions Options() => new() { Provider = "Oracle", ConnectionString = _fx.AppConnectionString };

    private async Task<DbConnection> OpenAsync()
    {
        var conn = await _factory.CreateOpenConnectionAsync(Options());
        await _sessions.InitializeSessionAsync(conn, "Oracle", new TenantId("tenant-" + Guid.NewGuid().ToString("N")[..8]), requireTransaction: false);
        return conn;
    }

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync();
        foreach (var ddl in new[]
        {
            $"CREATE TABLE {RawTable("Orders")} (ID NUMBER(10) PRIMARY KEY, TENANTID VARCHAR2(64) NOT NULL, REGION VARCHAR2(20) NOT NULL, STATUS VARCHAR2(20) NOT NULL, AMOUNT NUMBER(18,2) NOT NULL, EMAIL VARCHAR2(200), DUE DATE)",
            $"CREATE TABLE {RawTable("Entitlements")} (ID NUMBER(10) PRIMARY KEY, TENANTID VARCHAR2(64) NOT NULL, ORDERID NUMBER(10) NOT NULL)"
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        foreach (var seed in SeedStatements())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = seed;
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected override InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(OrdersId, ImmutableArray.Create(
            new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANTID", "VARCHAR2(64)"), new CatalogColumn("REGION", "VARCHAR2(20)"),
            new CatalogColumn("STATUS", "VARCHAR2(20)"), new CatalogColumn("AMOUNT", "NUMBER(18,2)"), new CatalogColumn("EMAIL", "VARCHAR2(200)"), new CatalogColumn("DUE", "DATE")),
            "TENANTID", 1),
        new TableCatalogEntry(EntitlementsId, ImmutableArray.Create(
            new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANTID", "VARCHAR2(64)"), new CatalogColumn("ORDERID", "NUMBER(10)")),
            "TENANTID", 1)
    }, AstCompilerOracleFixture.AppUser);

    protected override async Task<int> ExecuteAsync(CompiledSql compiled)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);   // DML runs in an explicit transaction
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        int affected = await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return affected;
    }

    protected override async Task<int> ExecuteInTransactionAsync(CompiledSql compiled) =>
        await RunCheckedAsync(await OpenAsync(), true, cmd => _binder.Bind(cmd, compiled, new Dictionary<string, object?>()), compiled);

    protected override async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var conn = await OpenAsync();
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

    // ---- Oracle specifics ----

    [Fact]
    public async Task TheRawSessionIsCaseInsensitive_ButTheGatewaySessionAndThePredicateAreExact()
    {
        // The hostile logon trigger makes a raw session linguistic and case-insensitive; the pinned gateway session and the byte-exact
        // tenant comparison keep acme and ACME apart even for DML.
        await using (var raw = new OracleConnection(_fx.AppConnectionString + ";Pooling=false"))
        {
            await raw.OpenAsync();
            await using var probe = new OracleCommand($"SELECT COUNT(*) FROM {RawTable("Orders")} WHERE TENANTID = 'acme'", raw);
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(5);
        }

        (await ExecAsync($"UPDATE {O} SET status = 'z' WHERE id > 0", "acme")).ShouldBe(3);
        (await ExecAsync($"DELETE FROM {O} WHERE id > 0", "ACME")).ShouldBe(2);
    }

    [Fact]
    public async Task Merge_UpdateWithAClauseCondition_UsesTheOracleWhereForm()
    {
        (await ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED AND s.id = 1 THEN UPDATE SET status = 'one'", "acme")).ShouldBe(1);
        (await OrdersAsync()).Where(r => r.Status == "one").Select(r => r.Id).ShouldBe(new[] { 1 });
    }

    [Fact]
    public async Task Merge_DeleteClause_AndRepeatedClauses_AreRejectedFailClosed_NothingChanges()
    {
        var before = await SnapshotAsync();
        await Should.ThrowAsync<SqlCompileNotSupportedException>(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN DELETE", "acme"));
        await Should.ThrowAsync<SqlCompileNotSupportedException>(() => ExecAsync(
            $"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED AND s.id = 1 THEN UPDATE SET status = 'a' WHEN MATCHED AND s.id = 3 THEN UPDATE SET status = 'b'", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task InList_OverTheOracleLimit_IsRejectedWithTheTypedError_BeforeExecution()
    {
        var items = string.Join(", ", Enumerable.Range(1, 1001));
        var ex = await Should.ThrowAsync<SqlLimitExceededException>(() => ExecAsync($"DELETE FROM {O} WHERE id IN ({items})", "acme"));
        ex.Kind.ShouldBe(SqlLimitKind.InListItems);
    }

    [Fact]
    public async Task Insert_EmptyTenant_IsRejectedByTheBinder_NothingIsWritten()
    {
        // Oracle treats '' as NULL (SEC-ADG-17): the binder refuses an empty tenant instead of letting NOT NULL or a predicate decide.
        var before = await SnapshotAsync();
        await RejectedAsync(() => ExecAsync($"UPDATE {O} SET status = 'x' WHERE id > 0", string.Empty));
        (await SnapshotAsync()).ShouldBe(before);
    }
}
