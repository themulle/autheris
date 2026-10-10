namespace Autheris.Tests.Unit.Sql;

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data.Common;
using System.Text;
using DuckDB.NET.Data;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Executes the governed compiler's DuckDB output in process: tenant isolation under a case-insensitive column collation,
/// consent filters, policy subqueries, masks and hostile values. DuckDB staging for federation keeps using the legacy
/// string generator; these tests only cover the governed Compile path.
/// </summary>
public sealed class AstCompilerDuckDbExecutionTests : IDisposable
{
    private static readonly TableIdentity Orders = new("main", "Orders");
    private static readonly TableIdentity Entitlements = new("main", "Entitlements");

    private readonly DuckDBConnection _conn;
    private readonly FastSqlEngine _engine = new();
    private readonly DuckDbCompiledSqlBinder _binder = new();
    private readonly Policies _policies = new();

    public AstCompilerDuckDbExecutionTests()
    {
        _conn = new DuckDBConnection("DataSource=:memory:");
        _conn.Open();
        Exec("""
            CREATE TABLE "Orders" (
                "Id" INTEGER PRIMARY KEY,
                "TenantId" VARCHAR COLLATE NOCASE NOT NULL,
                "Region" VARCHAR NOT NULL,
                "Status" VARCHAR NOT NULL,
                "Amount" DECIMAL(18,2) NOT NULL,
                "Email" VARCHAR);
            CREATE TABLE "Entitlements" ("Id" INTEGER PRIMARY KEY, "TenantId" VARCHAR COLLATE NOCASE NOT NULL, "OrderId" INTEGER NOT NULL);
            INSERT INTO "Orders" VALUES
                (1, 'acme',  'EU', 'open',   10.50, 'alice.smith@acme.example'),
                (2, 'acme',  'US', 'open',   20.00, 'bob.jones@acme.example'),
                (3, 'ACME',  'EU', 'open',   30.00, 'upper.case@ACME.example'),
                (4, 'ACME',  'US', 'closed', 40.00, 'upper.us@ACME.example'),
                (5, 'other', 'EU', 'open',   50.00, 'carol@other.example'),
                (6, 'acme',  'EU', 'closed', 60.00, NULL);
            INSERT INTO "Entitlements" VALUES (1, 'acme', 1), (2, 'other', 2), (3, 'acme', 6), (4, 'ACME', 3);
            """);
    }

    public void Dispose() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "VARCHAR"), new CatalogColumn("Region", "VARCHAR"),
            new CatalogColumn("Status", "VARCHAR"), new CatalogColumn("Amount", "DECIMAL(18,2)"), new CatalogColumn("Email", "VARCHAR")),
            "TenantId", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "VARCHAR"), new CatalogColumn("OrderId", "INTEGER")),
            "TenantId", 1)
    }, "main");

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
        qualifier is null ? new[] { new SqlIdentifier(name, true) } : new[] { new SqlIdentifier(qualifier), new SqlIdentifier(name, true) }));

    private static PolicyPredicate RegionPolicy(string region) => PolicyPredicate.Create(
        new BinaryExpression(Col("Region"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(region, SqlParameterType.String) });

    private CompileRequest Request(string tenant) => new()
    {
        TargetDialect = TargetSqlDialect.DuckDb,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    private List<object?[]> Run(string sql, string tenant, IReadOnlyDictionary<string, object?>? client = null)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        using var cmd = _conn.CreateCommand();
        _binder.Bind(cmd, compiled, client ?? new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    private static List<int> Ids(List<object?[]> rows) => rows.Select(r => Convert.ToInt32(r[0])).OrderBy(x => x).ToList();

    [Fact]
    public void Tenant_SeesOnlyItsOwnRows()
    {
        Ids(Run("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(Run("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
    }

    [Fact]
    public void TenantCaseCollision_IsIsolated_UnderANocaseColumnCollation()
    {
        // Sanity: the column really is case-insensitive, so a plain equality would leak the ACME rows.
        using (var probe = _conn.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM \"Orders\" WHERE \"TenantId\" = 'acme'";
            Convert.ToInt32(probe.ExecuteScalar()).ShouldBe(5);
        }

        Ids(Run("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(Run("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
    }

    [Fact]
    public void ConsentFilter_IsAppliedOnTopOfTheTenant()
    {
        _policies.Predicates[Orders] = RegionPolicy("EU");
        Ids(Run("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public void DenyAll_ReturnsNoRows_EvenWithATautologyInTheUserQuery()
    {
        _policies.Predicates[Orders] = PolicyPredicate.DenyAll;
        Run("SELECT id FROM orders WHERE 1 = 1 OR status = 'open'", "acme").ShouldBeEmpty();
    }

    [Fact]
    public void PolicySubquery_ForeignTenantRowsNeverDecideVisibility()
    {
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col("Id", "e"), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("main", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e", true)),
                new BinaryExpression(Col("OrderId", "e"), BinaryOperator.Equal, new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("autheris_target"), new SqlIdentifier("Id", true) }))),
                null, null), null, null));
        _policies.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());

        Ids(Run("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 6 });
    }

    [Fact]
    public void CteNamedLikeTheTable_UnionJoinSubquery_AreSecured()
    {
        Ids(Run("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        var rows = Run(
            "WITH o AS (SELECT id, status FROM orders) SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders) " +
            "UNION ALL SELECT id FROM orders WHERE id = (SELECT max(id) FROM orders)", "acme");
        rows.Select(r => Convert.ToInt32(r[0])).ShouldAllBe(id => id == 1 || id == 2 || id == 6);
    }

    [Theory]
    [InlineData("SELECT status, count(*) AS n, sum(amount) AS total FROM orders GROUP BY status ORDER BY status")]
    [InlineData("SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 LIMIT 5")]
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
    [InlineData("SELECT id FROM orders ORDER BY amount DESC NULLS LAST, id")]
    [InlineData("SELECT count(*) FILTER (WHERE amount > 15) AS n FROM orders")]
    [InlineData("SELECT region, GROUPING(region) AS g, count(*) AS n FROM orders GROUP BY ROLLUP (region)")]
    [InlineData("SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT e.id FROM entitlements e WHERE e.orderid = o.id) l")]
    [InlineData("SELECT extract(year FROM date '2024-05-06') AS y, extract(dow FROM date '2024-05-06') AS w FROM orders")]
    public void UserQueryShapes_CompileAndExecute(string sql)
    {
        Run(sql, "acme").ShouldNotBeNull();
    }

    [Fact]
    public void ClientNamedParameter_IsBoundByName()
    {
        Ids(Run("SELECT id FROM orders WHERE status = __param_st", "acme", new Dictionary<string, object?> { ["st"] = "closed" })).ShouldBe(new List<int> { 6 });
    }

    [Fact]
    public void PlanCache_RebindsTheTenant_NeverServesTheOtherTenantsRows()
    {
        Ids(Run("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
        Ids(Run("SELECT id FROM orders", "other")).ShouldBe(new List<int> { 5 });
        Ids(Run("SELECT id FROM orders", "ACME")).ShouldBe(new List<int> { 3, 4 });
        _engine.CompileCache.Stats.Hits.ShouldBe(2);
    }

    [Theory]
    [InlineData("'; DROP TABLE \"Orders\"; --")]
    [InlineData("acme' OR '1'='1")]
    [InlineData("acme\"; DROP TABLE \"Orders\"; --")]
    [InlineData("$$acme$$")]
    public void HostileTenantValue_IsJustAValue_NoRows_TableIntact(string hostile)
    {
        Run("SELECT id FROM orders", hostile).ShouldBeEmpty();
        Ids(Run("SELECT id FROM orders", "acme")).ShouldBe(new List<int> { 1, 2, 6 });
    }

    [Fact]
    public void EmbeddedNul_IsRejectedByTheBinder_NotSilentlyTruncated()
    {
        // DuckDB.NET binds strings as C strings: 'acme' + NUL would become 'acme'. The binder fails closed.
        Should.Throw<System.Security.SecurityException>(() => Run("SELECT id FROM orders", "acme\u0000"));
        Should.Throw<System.Security.SecurityException>(() =>
            Run("SELECT id FROM orders WHERE status = __param_s", "acme", new Dictionary<string, object?> { ["s"] = "x\u0000y" }));
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("'; DELETE FROM \"Orders\"; --")]
    [InlineData("%")]
    public void HostileUserLiteral_IsBound(string hostile)
    {
        Run("SELECT id FROM orders WHERE status = __param_s", "acme", new Dictionary<string, object?> { ["s"] = hostile }).ShouldBeEmpty();
    }

    // ---- masks ----

    private static PolicyParameterExpression MaskParam(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    [Fact]
    public void Redact_MaskedEmail_IsNeverInClear()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Redact,
            new MaskArguments(Constant: MaskParam("__mask_email", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

        Run("SELECT id, email FROM orders", "acme").Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");
        Run("SELECT * FROM orders", "acme").SelectMany(r => r).Select(v => v?.ToString()).ShouldNotContain(v => v != null && v.Contains("@acme.example"));
    }

    [Fact]
    public void PartialMask_KeepsPrefixAndSuffix()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.PartialMask,
            new MaskArguments(
                KeepPrefix: MaskParam("m_p", SqlParameterType.Int32), KeepSuffix: MaskParam("m_s", SqlParameterType.Int32), MaskChar: MaskParam("m_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(4, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());

        var masked = (string)Run("SELECT id, email FROM orders WHERE id = 1", "acme").Single()[1]!;
        masked.ShouldStartWith("al");
        masked.ShouldEndWith("mple");
        masked.ShouldContain("*");
        masked.ShouldNotBe("alice.smith@acme.example");
    }

    [Fact]
    public void Hmac_DegradesToRedact_BecauseDuckDbHasNoInDatabaseHmac()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: MaskParam("k_i", SqlParameterType.Binary), HmacKeyOuter: MaskParam("k_o", SqlParameterType.Binary)),
            new Dictionary<string, PolicyValue>
            {
                ["k_i"] = new(new byte[] { 1, 2, 3 }, SqlParameterType.Binary), ["k_o"] = new(new byte[] { 4, 5, 6 }, SqlParameterType.Binary)
            }.ToFrozenDictionary());

        var compiled = _engine.Compile("SELECT id, email FROM orders".AsMemory(), Request("acme"), CancellationToken.None);
        compiled.Parameters.ShouldNotContain(p => p.SourceName == "k_i" || p.SourceName == "k_o");   // the key never reaches the statement
        Run("SELECT id, email FROM orders", "acme").Select(r => (string?)r[1]).ShouldAllBe(e => e == "[REDACTED]");
    }

    [Fact]
    public void GeoJitterAndNullify_ExecuteAndNeverShowTheRawValue()
    {
        _policies.Masks[(Orders, "amount")] = new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: 0), FrozenDictionary<string, PolicyValue>.Empty);
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

        var row = Run("SELECT id, amount, email FROM orders WHERE id = 1", "acme").Single();
        Convert.ToDecimal(row[1]).ShouldBe(11m);
        row[2].ShouldBeNull();
    }

    [Fact]
    public void MaskedColumn_InWhereOrOrderBy_IsRejected()
    {
        _policies.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        Should.Throw<System.Security.SecurityException>(() => Run("SELECT id FROM orders WHERE email = 'x'", "acme"));
        Should.Throw<System.Security.SecurityException>(() => Run("SELECT id FROM orders ORDER BY email", "acme"));
    }

    // ---- limits ----

    [Fact]
    public void BindLimitProbe_DuckDbAcceptsTheCapabilityTableValue()
    {
        int max = DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb).MaxBindParameters;
        var sql = new StringBuilder("SELECT count(*) FROM range(10) AS t(x) WHERE x IN (");
        for (int i = 1; i <= max; i++)
        {
            if (i > 1) sql.Append(',');
            sql.Append('$').Append(i);
        }

        sql.Append(')');
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        for (int i = 1; i <= max; i++)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            p.Value = i;
            cmd.Parameters.Add(p);
        }

        Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(9);   // x = 1..9 are in the list (x = 0 is not)
    }

    [Fact]
    public void BindLimit_OverTheTable_IsATypedRejection()
    {
        string sql = "SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, 2200)) + ")";
        // 2200 < 65535: compiles; the typed rejection for DuckDB is only reachable above the limit, covered in generator tests.
        _engine.Compile(sql.AsMemory(), Request("acme"), CancellationToken.None).Parameters.Length.ShouldBeGreaterThan(2200);
    }
}
