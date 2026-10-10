namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Testcontainers.Oracle;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>One Oracle Free container with an application schema, a hostile logon trigger and a pool of size one.</summary>
public sealed class AstCompilerOracleFixture : IAsyncLifetime
{
    public const string AppUser = "AUTH_APP";
    public const string AppPassword = "App_Password_2026";

    private readonly OracleContainer _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithPassword("Oracle_Password_2026")
        .Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        string dataSource = $"{_container.Hostname}:{_container.GetMappedPublicPort(1521)}/FREEPDB1";
        AppConnectionString = $"User Id={AppUser};Password={AppPassword};Data Source={dataSource};Pooling=true;Max Pool Size=1;Min Pool Size=0";

        // The builder default user is not a DBA; SYSTEM uses the ORACLE_PASSWORD of the image.
        await using var admin = new OracleConnection($"User Id=system;Password=Oracle_Password_2026;Data Source={dataSource}");
        await admin.OpenAsync();
        foreach (var statement in new[]
        {
            $"CREATE USER {AppUser} IDENTIFIED BY {AppPassword} QUOTA UNLIMITED ON USERS",
            $"GRANT CREATE SESSION, ALTER SESSION, CREATE TABLE TO {AppUser}",
            $"""
            CREATE TABLE {AppUser}.ORDERS (
                ID NUMBER(10) PRIMARY KEY,
                TENANT_ID VARCHAR2(64) NOT NULL,
                REGION VARCHAR2(20) NOT NULL,
                STATUS VARCHAR2(20) NOT NULL,
                AMOUNT NUMBER(18,2) NOT NULL,
                EMAIL VARCHAR2(200))
            """,
            $"CREATE TABLE {AppUser}.ENTITLEMENTS (ID NUMBER(10) PRIMARY KEY, TENANT_ID VARCHAR2(64) NOT NULL, ORDER_ID NUMBER(10) NOT NULL)",
            $"""
            INSERT ALL
                INTO {AppUser}.ORDERS VALUES (1, 'acme',  'EU', 'open',   10.50, 'alice.smith@acme.example')
                INTO {AppUser}.ORDERS VALUES (2, 'acme',  'US', 'open',   20.00, 'bob.jones@acme.example')
                INTO {AppUser}.ORDERS VALUES (3, 'ACME',  'EU', 'open',   30.00, 'upper.case@ACME.example')
                INTO {AppUser}.ORDERS VALUES (4, 'ACME',  'US', 'closed', 40.00, 'upper.us@ACME.example')
                INTO {AppUser}.ORDERS VALUES (5, 'other', 'EU', 'open',   50.00, 'carol@other.example')
                INTO {AppUser}.ORDERS VALUES (6, 'acme',  'EU', 'closed', 60.00, NULL)
            SELECT 1 FROM DUAL
            """,
            $"""
            INSERT ALL
                INTO {AppUser}.ENTITLEMENTS VALUES (1, 'acme', 1)
                INTO {AppUser}.ENTITLEMENTS VALUES (2, 'other', 2)
                INTO {AppUser}.ENTITLEMENTS VALUES (3, 'acme', 6)
                INTO {AppUser}.ENTITLEMENTS VALUES (4, 'ACME', 3)
            SELECT 1 FROM DUAL
            """,
            "COMMIT"
        })
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = statement;
            try { await cmd.ExecuteNonQueryAsync(); } catch (OracleException ex) { throw new InvalidOperationException("Fixture statement failed: " + statement.Split((char)10)[0] + " => " + ex.Message, ex); }
        }

        // Hostile database defaults: every session of the application user starts case-insensitive and linguistic. The trigger
        // owner needs the privileges directly (roles do not apply inside definer-rights PL/SQL).
        await using (var grant = admin.CreateCommand())
        {
            grant.CommandText = "GRANT ALTER SESSION, ADMINISTER DATABASE TRIGGER TO SYSTEM";
            try { await grant.ExecuteNonQueryAsync(); } catch (OracleException ex) { throw new InvalidOperationException("Fixture GRANT failed => " + ex.Message, ex); }
        }

        await using (var trigger = admin.CreateCommand())
        {
            trigger.CommandText = $"""
                CREATE OR REPLACE TRIGGER SYSTEM.AUTH_APP_HOSTILE_LOGON AFTER LOGON ON {AppUser}.SCHEMA
                BEGIN
                  EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_COMP = LINGUISTIC';
                  EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_SORT = BINARY_CI';
                END;
                """;
            try { await trigger.ExecuteNonQueryAsync(); } catch (OracleException ex) { throw new InvalidOperationException("Fixture TRIGGER failed => " + ex.Message, ex); }
        }
    }

    public async Task DisposeAsync()
    {
        OracleConnection.ClearAllPools();
        await _container.DisposeAsync();
    }
}

internal sealed class DevelopmentEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// Executes the governed compiler's Oracle output on Oracle Free through the real gateway path: <see cref="SqlConnectionFactory"/>
/// (session pinning on every pool rental), <see cref="DbSessionContextInitializer"/> and <see cref="OracleCompiledSqlBinder"/>
/// with <c>BindByName</c>.
/// </summary>
public sealed class AstCompilerOracleExecutionTests : IClassFixture<AstCompilerOracleFixture>
{
    private static readonly TableIdentity Orders = new(AstCompilerOracleFixture.AppUser, "ORDERS");
    private static readonly TableIdentity Entitlements = new(AstCompilerOracleFixture.AppUser, "ENTITLEMENTS");

    private readonly AstCompilerOracleFixture _fx;
    private readonly FastSqlEngine _engine = new();
    private readonly OracleCompiledSqlBinder _binder = new();
    private readonly SqlConnectionFactory _factory = new(new DevelopmentEnvironment());
    private readonly DbSessionContextInitializer _sessions = new();
    private readonly Policies _policies = new();

    public AstCompilerOracleExecutionTests(AstCompilerOracleFixture fixture)
    {
        _fx = fixture;
    }

    private DataSourceConnectionOptions Options() => new() { Provider = "Oracle", ConnectionString = _fx.AppConnectionString };

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

    private static ColumnReference Col(string name, string? qualifier = null) => new(new SqlQualifiedName(
        qualifier is null ? new[] { new SqlIdentifier(name, true) } : new[] { new SqlIdentifier(qualifier, true), new SqlIdentifier(name, true) }));

    private static PolicyPredicate RegionPolicy(string region) => PolicyPredicate.Create(
        new BinaryExpression(Col("REGION"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(region, SqlParameterType.String) });

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

    private async Task<List<object?[]>> RunAsync(string sql, string tenant, IReadOnlyDictionary<string, object?>? client = null)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        await using var conn = await _factory.CreateOpenConnectionAsync(Options());
        await _sessions.InitializeSessionAsync(conn, "Oracle", new TenantId("tenant-" + Guid.NewGuid().ToString("N")[..8]), requireTransaction: false);
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, client ?? new Dictionary<string, object?>());
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

    private static List<int> Ids(List<object?[]> rows) => rows.Select(r => Convert.ToInt32(r[0])).OrderBy(x => x).ToList();

    // ---- CR-ADG-01: CTE names never shadow a physical table differently in the gateway and in the database ----

    [Theory]
    [InlineData("WITH \"orders\" AS (SELECT 1 AS id) SELECT id FROM orders")]
    [InlineData("WITH \"orders\" AS (SELECT 1 AS id) SELECT id FROM ORDERS")]
    [InlineData("WITH orders AS (SELECT 1 AS id) SELECT id FROM Orders")]
    public async Task QuotedCteVsUnquotedPhysical_CaseVariants_AlwaysSecured(string sql)
    {
        // Review reproduction: the gateway sees a CTE, Oracle bound the unquoted-upper-cased reference to the physical ORDERS table
        // and tenant "other" received rows 1-6 of all tenants. Now the reference is printed from the CTE definition.
        Ids(await RunAsync(sql, "other")).ShouldBe(new List<int> { 1 });
    }

    [Fact]
    public async Task CteBodyReadingThePhysicalTable_IsSecured()
    {
        Ids(await RunAsync("WITH \"orders\" AS (SELECT id FROM orders) SELECT id FROM \"orders\"", "other")).ShouldBe(new List<int> { 5 });
    }

    // ---- gateway path: session semantics (SEC-ADG-14, INV-14) ----

    [Fact]
    public async Task OracleSession_NlsPinned_OnEveryRental_AndIdentifierCleared()
    {
        // Sanity: the hostile logon trigger really makes a raw session case-insensitive.
        // Pooling=false: a pooled session may already have been pinned by an earlier test.
        await using (var raw = new OracleConnection(_fx.AppConnectionString + ";Pooling=false"))
        {
            await raw.OpenAsync();
            await using var probe = new OracleCommand("SELECT COUNT(*) FROM AUTH_APP.ORDERS WHERE TENANT_ID = 'acme'", raw);
            Convert.ToInt32(await probe.ExecuteScalarAsync()).ShouldBe(5);
        }

        OracleConnection.ClearAllPools();

        for (int rental = 0; rental < 3; rental++)
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(Options());
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT (SELECT value FROM nls_session_parameters WHERE parameter = 'NLS_COMP') || '|' || SYS_CONTEXT('USERENV','NLS_SORT') || '|' || SYS_CONTEXT('USERENV','NLS_DATE_FORMAT') FROM DUAL";
            ((string)(await check.ExecuteScalarAsync())!).ShouldBe("BINARY|BINARY|YYYY-MM-DD");

            // Dirty the pooled session and leave an identifier behind; the next rental must not see either.
            await using var dirty = conn.CreateCommand();
            dirty.CommandText = "BEGIN EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_COMP = LINGUISTIC'; DBMS_SESSION.SET_IDENTIFIER('leftover'); END;";
            await dirty.ExecuteNonQueryAsync();
        }

        await using var last = await _factory.CreateOpenConnectionAsync(Options());
        await using var identifier = last.CreateCommand();
        identifier.CommandText = "SELECT SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER') FROM DUAL";
        (await identifier.ExecuteScalarAsync()).ShouldBeOfType<DBNull>();
    }

    [Fact]
    public async Task SessionInitializer_SetsAnOpaqueIdentifier_NotTheTenant()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync(Options());
        await _sessions.InitializeSessionAsync(conn, "Oracle", new TenantId("tenant-secret-id"), requireTransaction: false);
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER') || '|' || SYS_CONTEXT('USERENV','MODULE') FROM DUAL";
        string value = (string)(await check.ExecuteScalarAsync())!;
        value.ShouldContain("|");
        value.ShouldNotContain("tenant-secret-id");
        value.ShouldEndWith("autheris");
    }

    [Fact]
    public async Task OffsetAndLimit_AreBoundToTheCorrectSlots_ByName()
    {
        // SEC-ADG-03 / F4: with positional binding the old order (limit before offset) returned the wrong page.
        await using var conn = await _factory.CreateOpenConnectionAsync(Options());
        await using var cmd = conn.CreateCommand();
        OracleBindByName.Enable(cmd);
        cmd.CommandText = "SELECT ID FROM AUTH_APP.ORDERS ORDER BY ID OFFSET :gql_offset ROWS FETCH NEXT :gql_limit ROWS ONLY";
        foreach (var (name, value) in new[] { ("gql_limit", 3), ("gql_offset", 0) })   // deliberately the "wrong" order
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(Convert.ToInt32(reader.GetValue(0)));
        ids.ShouldBe(new List<int> { 1, 2, 3 });
    }

    [Fact]
    public async Task CommandWithoutBindByName_IsRejectedByTheBinder_NotExecutedPositionally()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync(Options());
        await using var cmd = conn.CreateCommand();
        ((OracleCommand)cmd).BindByName.ShouldBeFalse();
        var compiled = _engine.Compile("SELECT id FROM orders".AsMemory(), Request("acme"), CancellationToken.None);
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        ((OracleCommand)cmd).BindByName.ShouldBeTrue();
    }

    // ---- RLS row visibility ----

    [Fact]
    public async Task Tenant_SeesOnlyItsOwnRows_UnderAHostileCaseInsensitiveSession()
    {
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
    }

    [Fact]
    public async Task ConsentFilter_DenyAll_AndPolicySubquery()
    {
        _policies.Predicates[Orders] = RegionPolicy("EU");
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });

        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        (await RunAsync("SELECT id FROM orders WHERE 1 = 1 OR status = 'open'", "acme")).ShouldBeEmpty();

        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col("ID", "e"), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier(AstCompilerOracleFixture.AppUser, true), new SqlIdentifier("ENTITLEMENTS", true) }), new SqlIdentifier("e", true)),
                new BinaryExpression(Col("ORDER_ID", "e"), BinaryOperator.Equal, new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("autheris_target"), new SqlIdentifier("ID", true) }))),
                null, null), null, null));
        _policies.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public async Task CteNamedLikeTheTable_UnionJoinSubquery_AreSecured()
    {
        Ids(await RunAsync("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        var rows = await RunAsync(
            "WITH o AS (SELECT id, status FROM orders) SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders) " +
            "UNION ALL SELECT id FROM orders WHERE id = (SELECT max(id) FROM orders)", "acme");
        rows.Select(r => Convert.ToInt32(r[0])).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
    }

    [Theory]
    [InlineData("SELECT status, count(*) AS n, sum(amount) AS total FROM orders GROUP BY status ORDER BY status")]
    [InlineData("SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 LIMIT 5")]
    [InlineData("SELECT id FROM orders ORDER BY id FETCH FIRST 2 ROWS WITH TIES")]
    [InlineData("SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY amount ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS rn FROM orders")]
    [InlineData("SELECT id, CASE WHEN amount > 15 THEN 'big' ELSE 'small' END AS size FROM orders")]
    [InlineData("SELECT id FROM orders WHERE email LIKE '%@acme.example' OR email IS NULL")]
    [InlineData("SELECT id, cast(amount AS integer) AS a, cast(status AS varchar) AS t FROM orders")]
    [InlineData("SELECT id, amount > 15 AS flag FROM orders")]
    [InlineData("SELECT id FROM orders WHERE id BETWEEN 1 AND 6 AND status IN ('open', 'closed') AND NOT (amount < 0)")]
    [InlineData("SELECT 1 AS x")]
    [InlineData("SELECT date '2024-01-15' AS d, timestamp '2024-01-15 10:30:00' AS ts FROM orders")]
    [InlineData("SELECT substring(email FROM 1 FOR 3) AS s, upper(status) AS u, length(status) AS l, strpos(email, '@') AS p FROM orders")]
    [InlineData("SELECT DISTINCT region FROM orders ORDER BY region")]
    [InlineData("SELECT id FROM orders GROUP BY id, status HAVING count(*) > 0")]
    [InlineData("SELECT id FROM orders ORDER BY amount DESC NULLS LAST, id")]
    [InlineData("SELECT region, GROUPING(region) AS g, count(*) AS n FROM orders GROUP BY ROLLUP (region)")]
    [InlineData("SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT e.id FROM entitlements e WHERE e.order_id = o.id) l")]
    [InlineData("SELECT extract(year FROM date '2024-05-06') AS y, extract(week FROM date '2024-05-06') AS w FROM orders")]
    [InlineData("SELECT a.id FROM orders a WHERE a.amount IS DISTINCT FROM 20.00")]
    public async Task UserQueryShapes_CompileAndExecute(string sql)
    {
        (await RunAsync(sql, "acme")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ClientNamedParameter_And_PlanCacheRebinding()
    {
        Ids(await RunAsync("SELECT id FROM orders WHERE status = __param_st", "acme", new Dictionary<string, object?> { ["st"] = "closed" })).ShouldBe(new List<int> { 6 });
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        _engine.CompileCache.Stats.Hits.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("'; DROP TABLE AUTH_APP.ORDERS; --")]
    [InlineData("acme' OR '1'='1")]
    [InlineData("q'[acme]'")]
    [InlineData("acme\"; DROP TABLE AUTH_APP.ORDERS; --")]
    [InlineData("acme\\")]
    public async Task HostileValues_AreJustValues(string hostile)
    {
        (await RunAsync("SELECT id FROM orders", hostile)).ShouldBeEmpty();
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        (await RunAsync("SELECT id FROM orders WHERE status = __param_s", "acme", new Dictionary<string, object?> { ["s"] = hostile })).ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyTenantString_IsRejected_BecauseOracleTreatsItAsNull()
    {
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders", string.Empty));
    }

    // ---- masks ----

    private static PolicyParameterExpression MaskParam(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    [Fact]
    public async Task Redact_Partial_GeoJitter_Nullify_AndHmacDegradation()
    {
        _policies.Masks[(Orders, "EMAIL")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: MaskParam("__mask_email", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());
        (await RunAsync("SELECT id, email FROM orders", "acme")).Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");
        (await RunAsync("SELECT * FROM orders", "acme")).SelectMany(r => r).Select(v => v?.ToString()).ShouldNotContain(v => v != null && v.Contains("@acme.example"));

        _policies.Masks[(Orders, "EMAIL")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(4, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());
        var masked = (string)(await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme")).Single()[1]!;
        masked.ShouldStartWith("al");
        masked.ShouldEndWith("mple");
        masked.ShouldContain("*");

        _policies.Masks[(Orders, "EMAIL")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(-3, SqlParameterType.Int32), ["m_s"] = new(0, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());
        var noSuffix = (string)(await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme")).Single()[1]!;
        noSuffix.ShouldNotContain("alice");
        noSuffix.ShouldNotContain("example");

        _policies.Masks[(Orders, "EMAIL")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: MaskParam("k_i", SqlParameterType.Binary), HmacKeyOuter: MaskParam("k_o", SqlParameterType.Binary)),
            new Dictionary<string, PolicyValue>
            {
                ["k_i"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["k_o"] = new(new byte[] { 2 }, SqlParameterType.Binary)
            }.ToFrozenDictionary());
        (await RunAsync("SELECT id, email FROM orders", "acme")).Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");

        _policies.Masks.Clear();
        _policies.Masks[(Orders, "AMOUNT")] = new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: 0), FrozenDictionary<string, PolicyValue>.Empty);
        _policies.Masks[(Orders, "EMAIL")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        var row = (await RunAsync("SELECT id, amount, email FROM orders WHERE id = 1", "acme")).Single();
        Convert.ToDecimal(row[1]).ShouldBe(11m);
        row[2].ShouldBeNull();
    }

    // ---- limits ----

    [Fact]
    public async Task OracleInList1001_Rejected_ByTheCompiler()
    {
        string sql = "SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, 1001)) + ")";
        var ex = await Should.ThrowAsync<SqlLimitExceededException>(() => RunAsync(sql, "acme"));
        ex.Kind.ShouldBe(SqlLimitKind.InListItems);
        // 1000 items are accepted by Oracle
        string ok = "SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, 1000)) + ")";
        Ids(await RunAsync(ok, "acme")).ShouldBe(new List<int> { 1, 2, 6 });
    }

    [Fact]
    public async Task OracleBindLimitProbe_AcceptsTheCapabilityTableValue()
    {
        int max = DialectCapabilityTable.Default.Get(TargetSqlDialect.Oracle).MaxBindParameters;
        var sql = new StringBuilder("SELECT COUNT(*) FROM DUAL WHERE ");
        for (int i = 1; i <= max; i++)
        {
            if (i > 1) sql.Append(" OR ");
            sql.Append(":p").Append(i).Append(" = 0");
        }

        await using var conn = await _factory.CreateOpenConnectionAsync(Options());
        await using var cmd = conn.CreateCommand();
        OracleBindByName.Enable(cmd);
        cmd.CommandText = sql.ToString();
        for (int i = 1; i <= max; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "p" + i;
            p.Value = 1;
            cmd.Parameters.Add(p);
        }

        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(0);
    }
}
