namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Executes the governed AST compiler's SQL Server output on a real SQL Server (Testcontainers or AUTHERIS_TEST_MSSQL):
/// RLS row visibility, exact tenant comparison under a case-insensitive collation, masks and hostile values.
/// Without a SQL Server the tests report as unavailable and return early like the other contract tests.
/// </summary>
public sealed class AstCompilerSqlServerFixture : IAsyncLifetime
{
    public SqlServerTestDatabase? Db { get; private set; }

    /// <summary>A second database with a case-sensitive collation (CR-ADG-01): <c>[orders]</c> and <c>[Orders]</c> are different names.</summary>
    public string CaseSensitiveConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        Db = await SqlServerTestDatabase.CreateAsync();
        if (!Db.IsAvailable) return;

        var csName = "autheris_cs_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder(Db.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var admin = new SqlConnection(master))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{csName}] COLLATE Latin1_General_100_CS_AS";
            await create.ExecuteNonQueryAsync();
        }

        CaseSensitiveConnectionString = new SqlConnectionStringBuilder(Db.ConnectionString) { InitialCatalog = csName }.ConnectionString;
        await using (var cs = new SqlConnection(CaseSensitiveConnectionString))
        {
            await cs.OpenAsync();
            await using var create = cs.CreateCommand();
            create.CommandText = """
                CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, TenantId nvarchar(64) NOT NULL, Region nvarchar(20) NOT NULL,
                    Status nvarchar(20) NOT NULL, Amount decimal(18,2) NOT NULL, Email nvarchar(200) NULL);
                INSERT dbo.Orders VALUES (1, N'acme', N'EU', N'open', 10.50, NULL), (2, N'acme', N'US', N'open', 20.00, NULL),
                    (5, N'other', N'EU', N'open', 50.00, NULL);
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using var conn = new SqlConnection(Db.ConnectionString);
        await conn.OpenAsync();
        const string ddl = """
            CREATE TABLE dbo.Orders (
                Id int NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                Region nvarchar(20) NOT NULL,
                Status nvarchar(20) NOT NULL,
                Amount decimal(18,2) NOT NULL,
                Email nvarchar(200) NULL);
            CREATE TABLE dbo.Entitlements (
                Id int NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                OrderId int NOT NULL);
            CREATE TABLE dbo.AnsiOrders (
                Id int NOT NULL PRIMARY KEY,
                TenantId varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE INDEX IX_AnsiOrders_TenantId ON dbo.AnsiOrders (TenantId);
            INSERT dbo.AnsiOrders VALUES (1, 'acme'), (2, 'ACME'), (3, 'other');
            INSERT dbo.Orders VALUES
                (1, N'acme',  N'EU', N'open',   10.50, N'alice.smith@acme.example'),
                (2, N'acme',  N'US', N'open',   20.00, N'bob.jones@acme.example'),
                (3, N'ACME',  N'EU', N'open',   30.00, N'upper.case@ACME.example'),
                (4, N'ACME',  N'US', N'closed', 40.00, N'upper.us@ACME.example'),
                (5, N'other', N'EU', N'open',   50.00, N'carol@other.example'),
                (6, N'acme',  N'EU', N'closed', 60.00, NULL);
            INSERT dbo.Entitlements VALUES
                (1, N'acme',  1), (2, N'other', 2), (3, N'acme', 6), (4, N'ACME', 3);
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (Db != null) await Db.DisposeAsync();
    }
}

public sealed class AstCompilerSqlServerExecutionTests : IClassFixture<AstCompilerSqlServerFixture>
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");
    private static readonly TableIdentity Entitlements = new("dbo", "Entitlements");

    private readonly SqlServerTestDatabase _db;
    private readonly AstCompilerSqlServerFixture _fixture;
    private readonly FastSqlEngine _engine = new();
    private readonly SqlServerCompiledSqlBinder _binder = new();
    private readonly Policies _policies = new();

    public AstCompilerSqlServerExecutionTests(AstCompilerSqlServerFixture fixture)
    {
        _db = fixture.Db!;
        _fixture = fixture;
    }

    // ---- fixtures ----

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Region", "nvarchar(20)"),
            new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "nvarchar(200)")),
            "TenantId", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")),
            "TenantId", 1)
    }, "dbo");

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }

    private static ColumnReference Col(string name, string? qualifier = null) => new(new SqlQualifiedName(
        (qualifier is null ? new[] { new SqlIdentifier(name, true) } : new[] { new SqlIdentifier(qualifier), new SqlIdentifier(name, true) })));

    private static PolicyPredicate RegionPolicy(string region) => PolicyPredicate.Create(
        new BinaryExpression(Col("Region"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(region, SqlParameterType.String) });

    private CompileRequest Request(string tenant) => new()
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    private async Task<List<object?[]>> RunAsync(string sql, string tenant, IReadOnlyDictionary<string, object?>? client = null, string? connectionString = null)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        await using var conn = new SqlConnection(connectionString ?? _db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, client ?? new Dictionary<string, object?>());
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

    private static List<int> Ids(List<object?[]> rows) => rows.Select(r => Convert.ToInt32(r[0])).OrderBy(x => x).ToList();

    // ---- CR-ADG-09: the tenant bind follows the catalog type of the compared column ----

    private static readonly TableIdentity AnsiOrders = new("dbo", "AnsiOrders");

    private static InMemoryTableCatalog AnsiCatalog(string tenantType) => new(new[]
    {
        new TableCatalogEntry(AnsiOrders, ImmutableArray.Create(new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", tenantType)), "TenantId", 1)
    }, "dbo");

    private CompiledSql CompileAnsi(string tenant, string tenantType) => _engine.Compile("SELECT id FROM ansiorders".AsMemory(), new CompileRequest
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = AnsiCatalog(tenantType),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    }, CancellationToken.None);

    private async Task<string> ShowPlanAsync(CompiledSql compiled)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using (var on = conn.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON";
            await on.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        await using var reader = await cmd.ExecuteReaderAsync();
        var plans = new System.Text.StringBuilder();
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server 2005 XML Showplan", StringComparison.Ordinal))
                {
                    plans.Append(reader.GetString(0));
                }
            }
        }
        while (await reader.NextResultAsync());

        plans.Length.ShouldBeGreaterThan(0, "the statement returned no execution plan");
        return plans.ToString();
    }

    [Fact]
    public async Task VarcharTenantColumn_IsBoundAsVarchar_NoImplicitConversionOfTheColumn()
    {
        if (!_db.IsAvailable) return;

        // Isolation still exact: acme and ACME are different tenants on a case-insensitive varchar column.
        Ids(await RunWith(CompileAnsi("acme", "varchar(64)"))).ShouldBe(new List<int> { 1 });
        Ids(await RunWith(CompileAnsi("ACME", "varchar(64)"))).ShouldBe(new List<int> { 2 });

        // Control: an nvarchar bind (what the binder produced before CR-ADG-09) converts the column, which turns the seek into a scan.
        (await ShowPlanAsync(CompileAnsi("acme", "nvarchar(64)"))).ShouldContain("CONVERT_IMPLICIT");
        (await ShowPlanAsync(CompileAnsi("acme", "varchar(64)"))).ShouldNotContain("CONVERT_IMPLICIT");
    }

    private async Task<List<object?[]>> RunWith(CompiledSql compiled)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    // ---- CR-ADG-01: CTE names never shadow a physical table differently in the gateway and in the database ----

    [Theory]
    [InlineData("WITH orders AS (SELECT 1 AS id) SELECT id FROM Orders")]
    [InlineData("WITH \"orders\" AS (SELECT 1 AS id) SELECT id FROM orders")]
    [InlineData("WITH Orders AS (SELECT 1 AS id) SELECT id FROM ORDERS")]
    public async Task QuotedCteVsUnquotedPhysical_CaseVariants_AlwaysSecured_OnACaseSensitiveDatabase(string sql)
    {
        if (!_db.IsAvailable) return;
        // The gateway sees a CTE; the database must see the same CTE, not the physical Orders (rows 1, 2 and 5).
        Ids(await RunAsync(sql, "other", connectionString: _fixture.CaseSensitiveConnectionString)).ShouldBe(new List<int> { 1 });
    }

    [Fact]
    public async Task CteBodyReadingThePhysicalTable_IsSecured_OnACaseSensitiveDatabase()
    {
        if (!_db.IsAvailable) return;
        Ids(await RunAsync("WITH orders AS (SELECT Id FROM dbo.Orders) SELECT Id FROM orders", "other", connectionString: _fixture.CaseSensitiveConnectionString))
            .ShouldBe(new List<int> { 5 });
    }

    // ---- RLS row visibility ----

    [Fact]
    public async Task Tenant_SeesOnlyItsOwnRows()
    {
        if (!_db.IsAvailable) return;
        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        _policies.Predicates.Remove(Orders); // no consent filter: tenant isolation only

        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
    }

    [Fact]
    public async Task TenantCaseCollision_IsIsolated_UnderACaseInsensitiveCollation()
    {
        if (!_db.IsAvailable) return;
        // The column collation is case-insensitive: a plain "TenantId = 'acme'" would also return the ACME rows 3 and 4.
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldNotContain(3);
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
    }

    [Fact]
    public async Task ConsentFilter_IsAppliedOnTopOfTheTenant()
    {
        if (!_db.IsAvailable) return;
        _policies.Predicates[Orders] = RegionPolicy("EU");
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public async Task DenyAll_ReturnsNoRows_EvenWithATautologyInTheUserQuery()
    {
        if (!_db.IsAvailable) return;
        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        (await RunAsync("SELECT id FROM orders WHERE 1 = 1 OR status = 'open'", "acme")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PolicySubquery_ForeignTenantRowsNeverDecideVisibility()
    {
        if (!_db.IsAvailable) return;
        // Visible when an entitlement exists for the order. Entitlement 2 belongs to tenant "other" and points at order 2 (acme):
        // without the tenant predicate inside the policy subquery, order 2 would become visible to acme.
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col("Id", "e"), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e", true)),
                new BinaryExpression(Col("OrderId", "e"), BinaryOperator.Equal, new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("autheris_target"), new SqlIdentifier("Id", true) }))),
                null, null), null, null));
        _policies.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());

        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public async Task UnionCteJoinSubquery_AllScopesAreSecured()
    {
        if (!_db.IsAvailable) return;
        var rows = await RunAsync(
            "WITH o AS (SELECT id, status FROM orders) " +
            "SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders) " +
            "UNION ALL SELECT id FROM orders WHERE id = (SELECT max(id) FROM orders)",
            "acme");
        rows.Select(r => Convert.ToInt32(r[0])).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
    }

    [Fact]
    public async Task CteNamedLikeTheTable_DoesNotLeakThePhysicalTable()
    {
        if (!_db.IsAvailable) return;
        // A self-reference needs WITH RECURSIVE (CR-ADG-32), so the body names the physical table schema-qualified; it still
        // resolves to dbo.Orders (secured) and the user-level CTE name is separate.
        var rows = await RunAsync("WITH orders AS (SELECT id FROM dbo.orders) SELECT id FROM orders", "other");
        Ids(rows).ShouldBe(new List<int> { 5 });
    }

    [Fact]
    public async Task ExplicitSchemaAndCatalogQualifiedNames_Resolve_ToTheSameSecuredTable()
    {
        if (!_db.IsAvailable) return;
        Ids(await RunAsync("SELECT id FROM dbo.Orders", "other")).ShouldBe(new List<int> { 5 });
        Ids(await RunAsync("SELECT id FROM \"DBO\".\"ORDERS\"", "other")).ShouldBe(new List<int> { 5 });
    }

    [Fact]
    public async Task CountStarFilter_IsEmulatedAndExecutes_WithinTheTenant()
    {
        if (!_db.IsAvailable) return;
        // CR-ADG-27: COUNT(*) FILTER is emulated with CASE WHEN ... THEN 1 END.
        var rows = await RunAsync("SELECT count(*) FILTER (WHERE id > 0) AS hit, count(*) FILTER (WHERE id > 1000) AS miss FROM orders", "other");
        Convert.ToInt32(rows[0][0]).ShouldBe(1);
        Convert.ToInt32(rows[0][1]).ShouldBe(0);
    }

    [Fact]
    public async Task RecursiveCte_ExecutesAndOnlyReadsTheTenantsRows()
    {
        if (!_db.IsAvailable) return;
        // CR-ADG-29: the CTE name resolves to itself; the anchor reads the secured table.
        var rows = await RunAsync(
            "WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT id, n + 1 FROM chain WHERE n < 3) SELECT id, n FROM chain",
            "acme");
        rows.ShouldNotBeEmpty();
        rows.Select(r => Convert.ToInt32(r[0])).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
        rows.Max(r => Convert.ToInt32(r[1])).ShouldBe(3);
        (rows.Count % 3).ShouldBe(0);
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync(
            "WITH RECURSIVE orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT id, n + 1 FROM orders WHERE n < 3) SELECT id FROM orders",
            "acme"));
    }

    // ---- user query shapes in bound mode ----

    [Theory]
    [InlineData("SELECT status, count(*) AS n, sum(amount) AS total FROM orders GROUP BY status ORDER BY status")]
    [InlineData("SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData("SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY amount ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS rn FROM orders")]
    [InlineData("SELECT id, CASE WHEN amount > 15 THEN 'big' ELSE 'small' END AS size FROM orders")]
    [InlineData("SELECT id FROM orders WHERE email LIKE '%@acme.example' OR email IS NULL")]
    [InlineData("SELECT id, cast(amount AS integer) AS a, try_cast(status AS integer) AS t FROM orders")]
    [InlineData("SELECT id, amount > 15 AS flag FROM orders")]
    [InlineData("SELECT id FROM orders WHERE id BETWEEN 1 AND 6 AND status IN ('open', 'closed') AND NOT (amount < 0)")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT date '2024-01-15' AS d, timestamp '2024-01-15 10:30:00' AS ts FROM orders")]
    [InlineData("SELECT substring(email FROM 1 FOR 3) AS s, upper(status) AS u, length(status) AS l FROM orders")]
    [InlineData("SELECT DISTINCT region FROM orders ORDER BY region")]
    [InlineData("SELECT id FROM orders GROUP BY id, status HAVING count(*) > 0")]
    public async Task UserQueryShapes_CompileAndExecute(string sql)
    {
        if (!_db.IsAvailable) return;
        (await RunAsync(sql, "acme")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ClientNamedParameter_IsBoundByName()
    {
        if (!_db.IsAvailable) return;
        var rows = await RunAsync("SELECT id FROM orders WHERE status = __param_st", "acme", new Dictionary<string, object?> { ["st"] = "closed" });
        Ids(rows).ShouldBe(new List<int> { 6 });
    }

    [Fact]
    public async Task PlanCache_RebindsTheTenant_NeverServesTheOtherTenantsRows()
    {
        if (!_db.IsAvailable) return;
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(await RunAsync("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        Ids(await RunAsync("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
        _engine.CompileCache.Stats.Hits.ShouldBe(2);
    }

    // ---- hostile values ----

    [Theory]
    [InlineData("'; DROP TABLE dbo.Orders; --")]
    [InlineData("acme' OR '1'='1")]
    [InlineData("acme]; DROP TABLE dbo.Orders; --")]
    [InlineData("acme\u0000")]
    public async Task HostileTenantValue_IsJustAValue_NoRows_TableIntact(string hostile)
    {
        if (!_db.IsAvailable) return;
        (await RunAsync("SELECT id FROM orders", hostile)).ShouldBeEmpty();
        Ids(await RunAsync("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("'; DELETE dbo.Orders; --")]
    [InlineData("%")]
    public async Task HostileUserLiteral_IsBound(string hostile)
    {
        if (!_db.IsAvailable) return;
        var compiled = _engine.Compile("SELECT id FROM orders WHERE status = __param_s".AsMemory(), Request("acme"), CancellationToken.None);
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["s"] = hostile });
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    // ---- masks ----

    private static PolicyParameterExpression MaskParam(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    [Fact]
    public async Task Redact_MaskedEmail_IsNeverInClear()
    {
        if (!_db.IsAvailable) return;
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: MaskParam("__mask_email", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

        var rows = await RunAsync("SELECT id, email FROM orders", "acme");
        rows.Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");
        // the raw value cannot be reached through the wildcard either
        var star = await RunAsync("SELECT * FROM orders", "acme");
        star.SelectMany(r => r).Select(v => v?.ToString()).ShouldNotContain(v => v != null && v.Contains("@acme.example"));
    }

    [Fact]
    public async Task PartialMask_KeepsPrefixAndSuffix()
    {
        if (!_db.IsAvailable) return;
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(
                KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(4, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());

        var rows = await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme");
        var masked = (string)rows.Single()[1]!;
        masked.ShouldStartWith("al");
        masked.ShouldEndWith("mple");
        masked.ShouldContain("*");
        masked.ShouldNotBe("alice.smith@acme.example");
    }

    [Fact]
    public async Task Hmac_MatchesTheReferenceHmacSha256()
    {
        if (!_db.IsAvailable) return;
        byte[] key = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        byte[] inner = key.Select(b => (byte)(b ^ 0x36)).ToArray();
        byte[] outer = key.Select(b => (byte)(b ^ 0x5c)).ToArray();
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: MaskParam("k_i", SqlParameterType.Binary), HmacKeyOuter: MaskParam("k_o", SqlParameterType.Binary)),
            new Dictionary<string, PolicyValue>
            {
                ["k_i"] = new(inner, SqlParameterType.Binary), ["k_o"] = new(outer, SqlParameterType.Binary)
            }.ToFrozenDictionary());

        var rows = await RunAsync("SELECT id, email FROM orders WHERE id = 1", "acme");
        string expected = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.Unicode.GetBytes("alice.smith@acme.example")));
        ((string)rows.Single()[1]!).ShouldBe(expected);
    }

    [Fact]
    public async Task GeoJitterAndNullify_ExecuteAndNeverShowTheRawValue()
    {
        if (!_db.IsAvailable) return;
        _policies.Masks[(Orders, "amount")] = new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: 0), FrozenDictionary<string, PolicyValue>.Empty);
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

        var rows = await RunAsync("SELECT id, amount, email FROM orders WHERE id = 1", "acme");
        Convert.ToDecimal(rows.Single()[1]).ShouldBe(11m);   // 10.50 rounded to 0 decimals
        rows.Single()[2].ShouldBeNull();
    }

    [Fact]
    public async Task MaskedColumn_InWhere_IsRejectedByTheCompiler()
    {
        if (!_db.IsAvailable) return;
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders WHERE email = 'x'", "acme"));
        await Should.ThrowAsync<System.Security.SecurityException>(() => RunAsync("SELECT id FROM orders ORDER BY email", "acme"));
    }
}
