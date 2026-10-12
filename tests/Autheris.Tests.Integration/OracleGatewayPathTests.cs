namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0z (WP-F5, M-10): Oracle Free on the gateway path, resolved from the real host container (<c>WebApplicationFactory&lt;Program&gt;</c>)
/// instead of hand-built services: <see cref="ISqlConnectionFactory"/> (session pinning on every pool rental),
/// <see cref="IDbSessionContextInitializer"/> and <see cref="ISqlEngine"/> from DI, plus the Oracle binder. The Oracle data source
/// is configured like an operator would (<c>Gateway:DataSources:Connections</c>) on a pool of size one, so every statement of every
/// tenant reuses the same physical session; the database defaults are hostile (linguistic, case-insensitive logon trigger).
/// Since X1.3 the WebSQL HTTP endpoint admits Oracle (SEC-X1-03): admission is derived from the capability tier and this suite is its evidence
/// (<c>Admission_OracleOnlyWithF5Evidence</c> pins the test names).
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class OracleGatewayPathTests : IDisposable
{
    private const string Source = OracleGatewayHost.Source;
    private static readonly TableIdentity Orders = new(AstCompilerOracleFixture.AppUser, "ORDERS");
    private static readonly TableIdentity Entitlements = new(AstCompilerOracleFixture.AppUser, "ENTITLEMENTS");

    private readonly AstCompilerOracleFixture _fx;
    private readonly WebApplicationFactory<Program> _host;
    private readonly ISqlConnectionFactory _connections;
    private readonly IDbSessionContextInitializer _sessions;
    private readonly ISqlEngine _engine;
    private readonly DataSourceConnectionOptions _options;
    private readonly ICompiledSqlBinder _binder = new OracleCompiledSqlBinder();
    private readonly Policies _policies = new();

    public OracleGatewayPathTests(AstCompilerOracleFixture fixture)
    {
        _fx = fixture;
        _host = OracleGatewayHost.Create(fixture, nameof(OracleGatewayPathTests));

        _connections = _host.Services.GetRequiredService<ISqlConnectionFactory>();
        _sessions = _host.Services.GetRequiredService<IDbSessionContextInitializer>();
        _engine = _host.Services.GetRequiredService<ISqlEngine>();
        _options = _host.Services.GetRequiredService<IOptions<GatewayOptions>>().Value.DataSources!.Connections![Source];
    }

    public void Dispose() => _host.Dispose();

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANT_ID", "VARCHAR2(64)"), new CatalogColumn("REGION", "VARCHAR2(20)"),
            new CatalogColumn("STATUS", "VARCHAR2(20)"), new CatalogColumn("AMOUNT", "NUMBER(18,2)"), new CatalogColumn("EMAIL", "VARCHAR2(200)")),
            "TENANT_ID", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANT_ID", "VARCHAR2(64)"), new CatalogColumn("ORDER_ID", "NUMBER(10)")),
            "TENANT_ID", 1)
    }, AstCompilerOracleFixture.AppUser);

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToUpperInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToUpperInvariant())];
    }

    private CompileRequest Request(string tenant) => new()
    {
        TargetDialect = TargetSqlDialect.Oracle,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    private async Task<List<object?[]>> RunAsync(string sql, string tenant, IReadOnlyDictionary<string, object?>? client = null, Func<DbConnection, Task>? dirtyAfter = null)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        await using var conn = await _connections.CreateOpenConnectionAsync(_options);
        await _sessions.InitializeSessionAsync(conn, "Oracle", new TenantId("tenant-" + Guid.NewGuid().ToString("N")[..8]), requireTransaction: false);
        var rows = new List<object?[]>();
        await using (var cmd = conn.CreateCommand())
        {
            _binder.Bind(cmd, compiled, client ?? new Dictionary<string, object?>());
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
            }
        }

        if (dirtyAfter is not null)
        {
            await dirtyAfter(conn);
        }

        return rows;
    }

    private static List<int> Ids(List<object?[]> rows) => rows.Select(r => Convert.ToInt32(r[0])).OrderBy(x => x).ToList();

    private static async Task ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // The hostile session state a previous rental can leave behind in a pooled physical connection.
    private static Task Dirty(DbConnection conn) => ScalarAsync(conn,
        "BEGIN EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_COMP = LINGUISTIC'; EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_SORT = BINARY_CI'; " +
        "EXECUTE IMMEDIATE 'ALTER SESSION SET CURRENT_SCHEMA = SYSTEM'; DBMS_SESSION.SET_IDENTIFIER('leftover'); END;");

    // ---- the services are the real ones ----

    [Fact]
    public async Task HostServices_AreTheRealGatewayImplementations_AndCommandsBindByName()
    {
        _connections.ShouldBeOfType<Autheris.Infrastructure.Persistence.SqlConnectionFactory>();
        _sessions.ShouldBeOfType<DbSessionContextInitializer>();
        _engine.ShouldBeOfType<FastSqlEngine>();

        await using var conn = await _connections.CreateOpenConnectionAsync(_options);
        await using var cmd = conn.CreateCommand();
        ((bool)cmd.GetType().GetProperty("BindByName")!.GetValue(cmd)!).ShouldBeTrue();
    }

    // ---- tenant isolation, pool reuse across tenants (M-10) ----

    [Fact]
    public async Task Tenants_AreIsolated_UnderAHostileDatabaseDefault()
    {
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        (await RunAsync("SELECT id FROM orders", "nobody")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PoolReuse_AcrossTenants_NeverLeaksSessionStateOrRows()
    {
        // Pool size one: all rentals share one physical session. Each rental leaves a dirty case-insensitive, foreign-schema,
        // identifier-carrying session behind; the next tenant must still get exactly its own rows.
        string[] tenants = ["acme", "ACME", "other", "acme", "ACME", "other"];
        var expected = new Dictionary<string, List<int>>
        {
            ["acme"] = new() { 1, 2, 6 }, ["ACME"] = new() { 3, 4 }, ["other"] = new() { 5 }
        };
        foreach (string tenant in tenants)
        {
            Ids(await RunAsync("SELECT id FROM orders", tenant, dirtyAfter: Dirty)).ShouldBe(expected[tenant]);
        }

        // A tenant-filtered join over a second table, still exact after the dirty rentals.
        Ids(await RunAsync("SELECT o.id FROM orders o JOIN entitlements e ON e.order_id = o.id", "acme", dirtyAfter: Dirty)).ShouldBe(new List<int> { 1, 6 });
        Ids(await RunAsync("SELECT o.id FROM orders o JOIN entitlements e ON e.order_id = o.id", "ACME", dirtyAfter: Dirty)).ShouldBe(new List<int> { 3 });
    }

    [Fact]
    public async Task PooledSession_AfterADirtyRental_IsPinnedAgain_BeforeAnyStatement()
    {
        await RunAsync("SELECT id FROM orders", "acme", dirtyAfter: Dirty);

        await using var conn = await _connections.CreateOpenConnectionAsync(_options);
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT (SELECT value FROM nls_session_parameters WHERE parameter = 'NLS_COMP') || '|' || SYS_CONTEXT('USERENV','NLS_SORT') || '|' || " +
                            "SYS_CONTEXT('USERENV','CURRENT_SCHEMA') || '|' || NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), '-') FROM DUAL";
        ((string)(await check.ExecuteScalarAsync())!).ShouldBe($"BINARY|BINARY|{AstCompilerOracleFixture.AppUser}|-");
    }

    // ---- masks ----

    private static PolicyParameterExpression MaskParam(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    [Fact]
    public async Task Masks_AreAppliedInTheDatabase_AndTheRawValueNeverLeavesIt()
    {
        _policies.Masks[(Orders, "EMAIL")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: MaskParam("__mask_email", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

        var rows = await RunAsync("SELECT id, email FROM orders", "acme");
        rows.Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");
        (await RunAsync("SELECT * FROM orders", "acme")).SelectMany(r => r).Select(v => v?.ToString())
            .ShouldNotContain(v => v != null && v.Contains("@acme.example"));
        // The mask does not weaken the row filter.
        Ids(rows).ShouldBe(new List<int> { 1, 2, 6 });
    }

    // ---- binds ----

    [Fact]
    public async Task ClientParameters_AreBoundByName_AndNeverBorrowedFromTenantOrPolicySlots()
    {
        var closed = await RunAsync("SELECT id FROM orders WHERE status = __param_st AND amount > __param_min", "acme",
            new Dictionary<string, object?> { ["st"] = "closed", ["min"] = 5 });
        Ids(closed).ShouldBe(new List<int> { 6 });

        // The same statement text for another tenant rebinds the tenant slot (plan cache) instead of reusing the first tenant's values.
        Ids(await RunAsync("SELECT id FROM orders WHERE status = __param_st AND amount > __param_min", "ACME",
            new Dictionary<string, object?> { ["st"] = "closed", ["min"] = 5 })).ShouldBe(new List<int> { 4 });
    }

    [Theory]
    [InlineData("'; DROP TABLE AUTH_APP.ORDERS; --")]
    [InlineData("q'[acme]'")]
    [InlineData("nq'[acme]'")]
    [InlineData("acme' OR '1'='1")]
    public async Task HostileValues_AreJustValues_ForTenantAndClientParameter(string hostile)
    {
        (await RunAsync("SELECT id FROM orders", hostile)).ShouldBeEmpty();
        (await RunAsync("SELECT id FROM orders WHERE status = __param_s", "acme", new Dictionary<string, object?> { ["s"] = hostile })).ShouldBeEmpty();
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
    }

    // ---- errors ----

    [Fact]
    public async Task MissingClientParameter_IsRejected_WithoutEchoingTenantValues()
    {
        var ex = await Should.ThrowAsync<MissingClientParameterException>(() => RunAsync("SELECT id FROM orders WHERE status = __param_st", "acme"));
        ex.Message.ShouldNotContain("acme");
    }

    [Fact]
    public async Task EmptyTenant_IsRejected_BecauseOracleTreatsAnEmptyStringAsNull()
    {
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders", string.Empty));
    }

    [Fact]
    public async Task TableOutsideTheCatalog_IsRejectedByTheCompiler_AndNeverReachesTheDatabase()
    {
        var ex = await Should.ThrowAsync<Exception>(() => RunAsync("SELECT table_name FROM all_tables", "acme"));
        ex.ShouldNotBeOfType<Oracle.ManagedDataAccess.Client.OracleException>();
        ex.Message.ShouldNotContain("acme");
    }

    [Fact]
    public async Task DatabaseError_AfterAnUnreachableSchema_FailsClosed_AndLeavesThePoolUsable()
    {
        var broken = new DataSourceConnectionOptions
        {
            Provider = "Oracle",
            ConnectionString = _fx.AppConnectionString,
            DefaultSchema = "NO_SUCH_SCHEMA_X0Z",
            RequireDefaultSchema = true
        };
        await Should.ThrowAsync<Oracle.ManagedDataAccess.Client.OracleException>(() => _connections.CreateOpenConnectionAsync(broken));

        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
    }

    // ---- WebSQL admission (X1.3, SEC-X1-03) ----

    [Fact]
    public async Task WebSqlEndpoint_AdmitsOracle_OnTheCompiledPath_WithTenantIsolationAndSanitizedErrors()
    {
        // X1.3: Oracle (tier Production) is admitted, because this suite (WP-F5 / M-10) is green. The HTTP door now runs the compiler, the
        // BindByName binder and the Oracle session pin; the engine-level cases above are the same path without HTTP.
        using (var scope = _host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            await repo.UpsertTableMetadataAsync(new Autheris.Domain.Model.TableMetadata
            {
                Table = new Autheris.Domain.Model.Table
                {
                    Id = Guid.NewGuid(), DisplayName = "ORDERS", TableName = "ORDERS", SchemaName = AstCompilerOracleFixture.AppUser, SourceName = Source, SourceType = "Oracle"
                },
                Identifier = new Autheris.Domain.Common.TableIdentifier(Source, AstCompilerOracleFixture.AppUser, "ORDERS"),
                Columns =
                [
                    new Autheris.Domain.Model.TableColumn { ColumnName = "ID", DataType = "int" },
                    new Autheris.Domain.Model.TableColumn { ColumnName = "TENANT_ID", DataType = "varchar" },
                    new Autheris.Domain.Model.TableColumn { ColumnName = "REGION", DataType = "varchar" },
                    new Autheris.Domain.Model.TableColumn { ColumnName = "EMAIL", DataType = "varchar", IsSensitive = true }
                ],
                ColumnMaskingRules = new Dictionary<string, Autheris.Domain.Model.MaskingRule> { ["EMAIL"] = new() { RuleType = "REDACT" } }
            });
        }

        async Task<System.Text.Json.JsonDocument> RunAs(string tenant, string sql)
        {
            var client = _host.CreateClient();
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
            var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql, dataSource = Source });
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            return System.Text.Json.JsonDocument.Parse(body);
        }

        static List<int> IdsOf(System.Text.Json.JsonDocument doc) =>
            doc.RootElement.GetProperty("rows").EnumerateArray()
                .Select(r => r.ValueKind == System.Text.Json.JsonValueKind.Array ? r[0].GetInt32() : r.GetProperty("ID").GetInt32()).OrderBy(x => x).ToList();

        using var acme = await RunAs("acme", $"SELECT id, email FROM {AstCompilerOracleFixture.AppUser}.ORDERS ORDER BY id");
        IdsOf(acme).ShouldBe(new List<int> { 1, 2, 6 });                                // 'ACME' (3, 4) and 'other' (5) are other tenants
        acme.RootElement.GetRawText().ShouldNotContain("alice.smith");                  // the raw value never leaves the database

        using var other = await RunAs("other", $"SELECT id FROM {AstCompilerOracleFixture.AppUser}.ORDERS ORDER BY id");
        IdsOf(other).ShouldBe(new List<int> { 5 });

        // a provider error is a stable code with a fixed message (ORA-01722 text never reaches the caller)
        var failing = _host.CreateClient();
        failing.DefaultRequestHeaders.Add("X-Tenant-Id", "acme");
        var error = await failing.PostAsJsonAsync("/api/v1/sql", new { sql = $"SELECT CAST(region AS integer) FROM {AstCompilerOracleFixture.AppUser}.ORDERS", dataSource = Source });
        var errorBody = await error.Content.ReadAsStringAsync();
        errorBody.ShouldNotContain("ORA-");
        errorBody.ShouldContain("code");
    }
}
