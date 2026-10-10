using System.Collections.Frozen;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-A7: INSERT, UPDATE, DELETE and MERGE through <c>ISqlEngine.Compile</c>. The security tests were written first (red: DML
/// was rejected as "not yet supported") and drive the typed DML injection. They run on every dialect that lists DML in its
/// capability entry.
/// </summary>
public class DmlCompileTests
{
    public static readonly TargetSqlDialect[] Dialects = { TargetSqlDialect.SqlServer, TargetSqlDialect.DuckDb, TargetSqlDialect.PostgreSql };

    public static IEnumerable<object[]> DialectData() => Dialects.Select(d => new object[] { d });

    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public DmlCompileTests()
    {
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _rowFilters.NoPolicy.Add(Lookup);
    }

    private static PolicyPredicate RegionPredicate(string value) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
            BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(value, SqlParameterType.String) });

    private void RegionPolicy()
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = RegionPredicate("EU");
    }

    private void MaskEmail() =>
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

    /// <summary>The fixture catalog with column types the dialect's generator knows (masks cast to the catalog type).</summary>
    private static InMemoryTableCatalog CatalogFor(TargetSqlDialect dialect)
    {
        if (dialect == TargetSqlDialect.SqlServer) return Catalog();
        return new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, System.Collections.Immutable.ImmutableArray.Create(
                new CatalogColumn("Id", "integer"), new CatalogColumn("TenantId", "varchar"), new CatalogColumn("Region", "varchar"),
                new CatalogColumn("Status", "varchar"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "varchar")),
                "TenantId", 1),
            new TableCatalogEntry(Entitlements, System.Collections.Immutable.ImmutableArray.Create(
                new CatalogColumn("Id", "integer"), new CatalogColumn("TenantId", "varchar"), new CatalogColumn("OrderId", "integer")),
                "TenantId", 1),
            new TableCatalogEntry(Lookup, System.Collections.Immutable.ImmutableArray.Create(new CatalogColumn("Code", "varchar")), null, 1)
        }, "dbo");
    }

    private CompileRequest Request(
        TargetSqlDialect dialect,
        string tenant = "acme",
        StatementPermissions statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        DmlGuardOptions? dml = null) => new()
    {
        TargetDialect = dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = statements,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = CatalogFor(dialect),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String),
            Dml = dml ?? DmlGuardOptions.Strict
        }
    };

    private CompiledSql Compile(TargetSqlDialect dialect, string sql, string tenant = "acme", DmlGuardOptions? dml = null) =>
        _engine.Compile(sql.AsMemory(), Request(dialect, tenant, dml: dml), CancellationToken.None);

    private void Rejected(TargetSqlDialect dialect, string sql, DmlGuardOptions? dml = null) =>
        Assert.ThrowsAny<Exception>(() => Compile(dialect, sql, dml: dml));

    private void RejectedSecurity(TargetSqlDialect dialect, string sql, DmlGuardOptions? dml = null) =>
        Assert.ThrowsAny<SecurityException>(() => Compile(dialect, sql, dml: dml));

    private static int Tenants(CompiledSql c) => c.Parameters.Count(p => p.Origin == ParameterOrigin.Tenant);

    // ---- permissions and capability gating ----

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Dml_WithoutPermission_IsRejected_WithATypedError(TargetSqlDialect dialect)
    {
        var request = Request(dialect, statements: StatementPermissions.ReadOnly);
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() =>
            _engine.Compile("DELETE FROM orders WHERE id = 1".AsMemory(), request, CancellationToken.None));
        Assert.Equal(SqlCompileNotSupportedReason.StatementClass, ex.Reason);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void EachStatementClass_NeedsItsOwnPermission(TargetSqlDialect dialect)
    {
        var onlyDelete = Request(dialect, statements: StatementPermissions.Delete);
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            _engine.Compile("UPDATE orders SET status = 'x' WHERE id = 1".AsMemory(), onlyDelete, CancellationToken.None));
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            _engine.Compile("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')".AsMemory(), onlyDelete, CancellationToken.None));
        var c = _engine.Compile("DELETE FROM orders WHERE id = 1".AsMemory(), onlyDelete, CancellationToken.None);
        Assert.Equal(SqlStatementClass.Delete, c.StatementClass);
    }

    [Fact]
    public void UnknownPermissionBits_AreRejected()
    {
        var request = Request(TargetSqlDialect.SqlServer, statements: (StatementPermissions)64);
        Assert.Throws<SqlCompileNotSupportedException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    // ---- INSERT ----

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertValues_ForcesTheBoundTenant_NeverTheLiteral(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open'), (2, 'acme', 'closed')");
        Assert.Equal(SqlStatementClass.Insert, c.StatementClass);
        Assert.Equal(1, Tenants(c));   // both rows share the one bound tenant marker
        Assert.DoesNotContain("acme", c.Sql);
        Assert.Contains("Orders", c.Sql);
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "acme"));
        Assert.DoesNotContain(c.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "acme"));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertValues_TenantOfAnotherTenant_IsRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'other')");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'other')");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'ACME')");   // exact, not case-insensitive
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme ')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertValues_TenantAsExpressionOrParameter_IsRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, __param_t)");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, lower('ACME'))");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, (SELECT tenantid FROM orders))");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, NULL)");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_TenantColumnMissing_IsRejected_UnderStrict_AndForced_WhenNotRequired(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO orders (id, status) VALUES (1, 'open')");
        var relaxed = DmlGuardOptions.Strict with { RequireTenantColumnInInsert = false };
        var c = Compile(dialect, "INSERT INTO orders (id, status) VALUES (1, 'open')", dml: relaxed);
        Assert.Equal(1, Tenants(c));
        Assert.Contains("TenantId", c.Sql);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_WithoutColumnList_IsRejected(TargetSqlDialect dialect) =>
        RejectedSecurity(dialect, "INSERT INTO orders VALUES (1, 'acme', 'EU', 'open', 1.5, 'x')");

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_ValueCountMismatch_IsRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme')");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme', 'x')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_MaskedColumn_IsRejected(TargetSqlDialect dialect)
    {
        MaskEmail();
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, email) VALUES (1, 'acme', 'a@b.c')");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, EMAIL) VALUES (1, 'acme', 'a@b.c')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_IntoATableWithARowPolicy_IsRejected(TargetSqlDialect dialect)
    {
        RegionPolicy();
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_UnknownTableOrColumnOrDuplicateColumn_IsRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO mystery (id) VALUES (1)");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, nope) VALUES (1, 'acme', 2)");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, id) VALUES (1, 'acme', 2)");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertSelect_SourceIsSecuredLikeDql_AndTenantIsForced(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid, status) SELECT id, 'acme', status FROM entitlements WHERE id > 5");
        Assert.Equal(SqlStatementClass.Insert, c.StatementClass);
        Assert.Equal(1, Tenants(c));
        Assert.DoesNotContain("acme", c.Sql);
        Assert.Contains("Entitlements", c.Sql);
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertSelect_TenantFromTheSourceColumn_OrWildcard_IsRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) SELECT id, tenantid FROM orders");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) SELECT * FROM orders");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) SELECT id, 'other' FROM orders");
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) SELECT 1, 'acme', 3 FROM orders");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertSelect_UnionBranches_EachCarryTheTenant(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders UNION ALL SELECT id, 'acme' FROM entitlements");
        Assert.Equal(1, Tenants(c));
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders UNION ALL SELECT id, 'other' FROM entitlements");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void InsertSelect_MaskedSourceColumn_InsertsTheMaskedValueOnly(TargetSqlDialect dialect)
    {
        MaskEmail();
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid, status) SELECT id, 'acme', email FROM orders");
        Assert.Contains("NULL", c.Sql);   // the secured derived table projects the mask, never the raw column
        Assert.DoesNotContain("acme", c.Sql);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_SourceWithOrderByLimitOrWith_IsRejected(TargetSqlDialect dialect)
    {
        Rejected(dialect, "INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders ORDER BY id");
        Rejected(dialect, "INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders LIMIT 3");
        Rejected(dialect, "INSERT INTO orders (id, tenantid) WITH x AS (SELECT id FROM orders) SELECT id, 'acme' FROM x");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_MultiRow_OverTheBindLimit_IsRejected_WithTheTypedError(TargetSqlDialect dialect)
    {
        _engine.MaxQueryLength = 8_000_000;   // the text guard (64 K characters) would otherwise fire before the bind limit of the larger dialects
        var max = DialectCapabilityTable.Default.Get(dialect).MaxBindParameters;
        int rows = (max / 2) + 5;   // id + status per row; the tenant marker is shared
        var sql = "INSERT INTO orders (id, tenantid, status) VALUES " +
                  string.Join(", ", Enumerable.Range(1, rows).Select(i => $"({i}, 'acme', 's{i}')"));
        var request = Request(dialect) with { CompileTimeout = TimeSpan.FromSeconds(25) };   // a huge statement must reach the bind limit, not the 2 s budget
        var ex = Assert.Throws<SqlLimitExceededException>(() => _engine.Compile(sql.AsMemory(), request, CancellationToken.None));
        Assert.Equal(SqlLimitKind.BindParameters, ex.Kind);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void DmlWhere_InList_OverTheDialectLimit_IsRejected_WithTheTypedError(TargetSqlDialect dialect)
    {
        var max = DialectCapabilityTable.Default.Get(dialect).MaxInListItems;
        if (max is null) return;   // the dialect has no separate IN-list limit; the bind limit covers it
        var items = string.Join(", ", Enumerable.Range(1, max.Value + 1));
        var ex = Assert.Throws<SqlLimitExceededException>(() => Compile(dialect, $"DELETE FROM orders WHERE id IN ({items})"));
        Assert.Equal(SqlLimitKind.InListItems, ex.Kind);
        ex = Assert.Throws<SqlLimitExceededException>(() => Compile(dialect, $"UPDATE orders SET status = 'x' WHERE id IN ({items})"));
        Assert.Equal(SqlLimitKind.InListItems, ex.Kind);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_Returning_And_Output_AreParseRejected(TargetSqlDialect dialect)
    {
        Rejected(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme') RETURNING *");
        Rejected(dialect, "INSERT INTO orders (id, tenantid) OUTPUT inserted.* VALUES (1, 'acme')");
    }

    // ---- UPDATE ----

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_InjectsTenantAndPolicyIntoTheWhere(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var c = Compile(dialect, "UPDATE orders SET status = 'closed' WHERE id = 7");
        Assert.Equal(SqlStatementClass.Update, c.StatementClass);
        Assert.Equal(1, Tenants(c));
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), c.AppliedPredicates);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 1), c.AppliedPredicates);
        Assert.DoesNotContain("acme", c.Sql);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_UserOrCannotAbsorbTheInjectedPredicates(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "UPDATE orders SET status = 'x' WHERE id = 1 OR id = 2");
        int where = c.Sql.IndexOf("WHERE", StringComparison.Ordinal);
        Assert.Contains("(", c.Sql[where..]);
        Assert.Matches(@"WHERE \(.*\bOR\b.*\) AND \(", c.Sql);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_WithoutWhere_OrWithATautology_IsRejected_AsUnfilteredDml(TargetSqlDialect dialect)
    {
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "UPDATE orders SET status = 'x'"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "UPDATE orders SET status = 'x' WHERE 1 = 1"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "UPDATE orders SET status = 'x' WHERE id = 1 OR 1 = 1"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "UPDATE orders SET status = 'x' WHERE true"));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_TenantColumnAssignment_IsRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "UPDATE orders SET tenantid = 'other' WHERE id = 1");
        RejectedSecurity(dialect, "UPDATE orders SET TENANTID = 'acme' WHERE id = 1");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_PolicyReferencedColumnAssignment_IsRejected_SecAdg06(TargetSqlDialect dialect)
    {
        RegionPolicy();
        RejectedSecurity(dialect, "UPDATE orders SET region = 'US' WHERE id = 7");
        RejectedSecurity(dialect, "UPDATE orders SET REGION = 'US' WHERE id = 7");
        RejectedSecurity(dialect, "UPDATE orders SET status = 'x', region = 'US' WHERE id = 7");
        // other columns stay writable
        Assert.Equal(SqlStatementClass.Update, Compile(dialect, "UPDATE orders SET status = 'x' WHERE id = 7").StatementClass);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_PolicyColumnAssignment_IsAllowed_WhenTheGuardIsSwitchedOff(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var relaxed = DmlGuardOptions.Strict with { RejectPolicyColumnAssignment = false };
        Assert.Equal(SqlStatementClass.Update, Compile(dialect, "UPDATE orders SET region = 'US' WHERE id = 7", dml: relaxed).StatementClass);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_MaskedColumn_CannotBeWritten_OrReadInSetOrWhere(TargetSqlDialect dialect)
    {
        MaskEmail();
        RejectedSecurity(dialect, "UPDATE orders SET email = 'x' WHERE id = 1");
        RejectedSecurity(dialect, "UPDATE orders SET status = email WHERE id = 1");
        RejectedSecurity(dialect, "UPDATE orders SET status = upper(Email) WHERE id = 1");
        RejectedSecurity(dialect, "UPDATE orders SET status = 'x' WHERE email = 'a@b.c'");
        RejectedSecurity(dialect, "UPDATE orders SET status = 'x' WHERE id = 1 AND orders.email LIKE 'a%'");
        RejectedSecurity(dialect, "UPDATE orders SET status = 'x' WHERE id IN (SELECT id FROM entitlements WHERE orders.email = 'a')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_CorrelatedRowPolicy_IsRejected(TargetSqlDialect dialect)
    {
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e"), new SqlIdentifier("Id", true) })), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e")),
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e"), new SqlIdentifier("OrderId", true) })),
                    BinaryOperator.Equal,
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("Id", true) }))),
                null, null), null, null));
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());
        RejectedSecurity(dialect, "UPDATE orders SET status = 'x' WHERE id = 1");
        RejectedSecurity(dialect, "DELETE FROM orders WHERE id = 1");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_SubqueriesInWhereAndSet_AreSecuredOnEverySource(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE id IN (SELECT orderid FROM entitlements WHERE id > 3)");
        Assert.Equal(1, Tenants(c));
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
        Assert.True(CountOf(c.Sql, "[Entitlements]", "\"Entitlements\"") >= 2);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_UnknownTarget_UnknownColumn_DuplicateAssignment_AreRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "UPDATE mystery SET a = 1 WHERE b = 2");
        RejectedSecurity(dialect, "UPDATE orders SET nope = 1 WHERE id = 2");
        RejectedSecurity(dialect, "UPDATE orders SET status = 'a', STATUS = 'b' WHERE id = 2");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_From_Join_Returning_Output_AreParseRejected(TargetSqlDialect dialect)
    {
        Rejected(dialect, "UPDATE orders SET status = 'x' FROM entitlements WHERE orders.id = entitlements.orderid");
        Rejected(dialect, "UPDATE orders SET status = 'x' WHERE id = 1 RETURNING *");
        Rejected(dialect, "UPDATE orders SET status = 'x' OUTPUT deleted.* WHERE id = 1");
        Rejected(dialect, "UPDATE orders o JOIN entitlements e ON o.id = e.orderid SET status = 'x' WHERE o.id = 1");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_TableBranchSuffix_IsRejected(TargetSqlDialect dialect) =>
        Rejected(dialect, "UPDATE orders@main SET status = 'x' WHERE id = 1");

    // ---- DELETE ----

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Delete_InjectsTenantAndPolicy_AndRejectsUnfilteredScope(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var c = Compile(dialect, "DELETE FROM orders WHERE id = 7");
        Assert.Equal(SqlStatementClass.Delete, c.StatementClass);
        Assert.Equal(1, Tenants(c));
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "DELETE FROM orders"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "DELETE FROM orders WHERE 1 = 1"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "DELETE FROM orders WHERE id = id"));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Delete_DenyAllPolicy_StaysInTheWhere(TargetSqlDialect dialect)
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.DenyAll;
        var c = Compile(dialect, "DELETE FROM orders WHERE id = 7 OR status = 'x'");
        Assert.Contains("1 = 0", c.Sql);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Delete_MaskedColumnInWhere_Using_Returning_AreRejected(TargetSqlDialect dialect)
    {
        MaskEmail();
        RejectedSecurity(dialect, "DELETE FROM orders WHERE email = 'a@b.c'");
        RejectedSecurity(dialect, "DELETE FROM orders WHERE id IN (SELECT id FROM entitlements WHERE orders.EMAIL = 'x')");
        Rejected(dialect, "DELETE FROM orders USING entitlements WHERE orders.id = entitlements.orderid");
        Rejected(dialect, "DELETE FROM orders WHERE id = 1 RETURNING id");
        Rejected(dialect, "DELETE FROM orders OUTPUT deleted.* WHERE id = 1");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Delete_SubqueryInWhere_IsSecured(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "DELETE FROM orders WHERE id IN (SELECT orderid FROM entitlements)");
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
        Assert.Equal(1, Tenants(c));
    }

    // ---- MERGE ----

    private const string MergeUpdateDelete =
        "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
        "WHEN MATCHED AND s.id > 1 THEN UPDATE SET status = 'merged' WHEN MATCHED THEN DELETE";

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_TargetPredicatesGoIntoOn_AndTheSourceIsSecured(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var c = Compile(dialect, MergeUpdateDelete);
        Assert.Equal(SqlStatementClass.Merge, c.StatementClass);
        Assert.Equal(1, Tenants(c));
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), c.AppliedPredicates);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 1), c.AppliedPredicates);
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
        Assert.DoesNotContain("acme", c.Sql);
        Assert.DoesNotContain("BY SOURCE", c.Sql, StringComparison.OrdinalIgnoreCase);
        int on = c.Sql.IndexOf(" ON ", StringComparison.Ordinal);
        int when = c.Sql.IndexOf(" WHEN ", StringComparison.Ordinal);
        Assert.True(on > 0 && when > on);
        Assert.Contains("TenantId", c.Sql[on..when]);   // the target's tenant predicate is part of the ON condition
    }

    [Fact]
    public void Merge_SqlServer_EndsWithExactlyOneSemicolon_AtTheFinalPosition()
    {
        var c = Compile(TargetSqlDialect.SqlServer, MergeUpdateDelete);
        Assert.EndsWith(";", c.Sql);
        Assert.Equal(1, c.Sql.Count(ch => ch == ';'));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_Insert_ForcesTheBoundTenant(TargetSqlDialect dialect)
    {
        var c = Compile(dialect,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, 'acme', 'new')");
        Assert.Equal(1, Tenants(c));
        Assert.DoesNotContain("acme", c.Sql);
        RejectedSecurity(dialect,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, 'other', 'new')");
        RejectedSecurity(dialect,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, s.tenantid, 'new')");
        RejectedSecurity(dialect,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, status) VALUES (s.id, 'new')");
        RejectedSecurity(dialect,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT VALUES (s.id, 'acme', 'new')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void MergeInsert_OnATableWithARowPolicy_IsRejected(TargetSqlDialect dialect)
    {
        RegionPolicy();
        RejectedSecurity(dialect,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid) VALUES (s.id, 'acme')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void MergeUpdate_TenantMaskedOrPolicyColumn_IsRejected(TargetSqlDialect dialect)
    {
        RegionPolicy();
        MaskEmail();
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET tenantid = 'other'");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET region = 'US'");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET email = 'x'");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_MaskedColumnInOnWhenOrAssignment_IsRejected(TargetSqlDialect dialect)
    {
        MaskEmail();
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.email = 'x' WHEN MATCHED THEN DELETE");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED AND t.email = 'x' THEN DELETE");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = t.email");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = email");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED AND t.email IS NULL THEN INSERT (id, tenantid) VALUES (s.id, 'acme')");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, 'acme', t.email)");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_TriviallyTrueOrColumnFreeOn_IsRejected_AsUnfilteredDml(TargetSqlDialect dialect)
    {
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "MERGE INTO orders t USING entitlements s ON 1 = 1 WHEN MATCHED THEN DELETE"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "MERGE INTO orders t USING entitlements s ON true WHEN MATCHED THEN DELETE"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid OR 1 = 1 WHEN MATCHED THEN DELETE"));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_CorrelatedTargetRowPolicy_IsRejected(TargetSqlDialect dialect)
    {
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e"), new SqlIdentifier("Id", true) })), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e")),
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e"), new SqlIdentifier("OrderId", true) })),
                    BinaryOperator.Equal,
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("Id", true) }))),
                null, null), null, null));
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());
        RejectedSecurity(dialect, MergeUpdateDelete);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_NotMatchedBySource_AndByTarget_AreParseRejected(TargetSqlDialect dialect)
    {
        Rejected(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED BY SOURCE THEN DELETE");
        Rejected(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED BY SOURCE AND t.id > 1 THEN DELETE");
        Rejected(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED BY TARGET THEN INSERT (id, tenantid) VALUES (1, 'acme')");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_SourceIsSecured_OtherTenantRowsAreNeverReadFromTheSource(TargetSqlDialect dialect)
    {
        var c = Compile(dialect,
            "MERGE INTO orders t USING (SELECT orderid, id FROM entitlements WHERE id > 0) s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'");
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
        var joined = Compile(dialect,
            "MERGE INTO orders t USING orders s ON t.id = s.id WHEN MATCHED AND s.status = 'a' THEN UPDATE SET status = 'b'");
        Assert.True(CountOf(joined.Sql, "[Orders]", "\"Orders\"") >= 2);   // target and source are both the physical table
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_TargetWithoutAlias_UsesTheTableNameAsTheQualifier(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "MERGE INTO orders USING entitlements s ON orders.id = s.orderid WHEN MATCHED THEN DELETE");
        Assert.Equal(SqlStatementClass.Merge, c.StatementClass);
        Assert.Equal(1, Tenants(c));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_UnknownTarget_AndTableBranch_AreRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "MERGE INTO mystery t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN DELETE");
        Rejected(dialect, "MERGE INTO orders@main t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN DELETE");
    }

    // ---- invariants of the plan cache and the verifier ----

    [Theory]
    [MemberData(nameof(DialectData))]
    public void DmlTemplates_AreValueFree_AndRebindTheTenantOnAHit(TargetSqlDialect dialect)
    {
        var first = Compile(dialect, "DELETE FROM orders WHERE id = 7", "tenant-one");
        var second = Compile(dialect, "DELETE FROM orders WHERE id = 7", "tenant-two");
        Assert.Equal(first.Sql, second.Sql);
        Assert.Contains(second.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "tenant-two"));
        Assert.DoesNotContain(second.Parameters, p => Equals(p.Value, "tenant-one"));
        Assert.Equal(1, _engine.CompileCache.Stats.Hits);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void ErrorMessages_NeverEchoTheTenantValue(TargetSqlDialect dialect)
    {
        var ex = Assert.ThrowsAny<SecurityException>(() => Compile(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'victim-tenant')", "caller-tenant"));
        Assert.DoesNotContain("victim-tenant", ex.Message);
        Assert.DoesNotContain("caller-tenant", ex.Message);
    }

    private static int CountOf(string text, params string[] needles) =>
        needles.Sum(n => text.Split(n).Length - 1);
}
