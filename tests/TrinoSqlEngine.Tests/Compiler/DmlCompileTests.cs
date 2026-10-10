using System.Collections.Immutable;
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
    public static readonly TargetSqlDialect[] Dialects = { TargetSqlDialect.SqlServer, TargetSqlDialect.DuckDb, TargetSqlDialect.PostgreSql, TargetSqlDialect.Databricks, TargetSqlDialect.Oracle };

    public static IEnumerable<object[]> DialectData() => Dialects.Select(d => new object[] { d });

    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();
    private InMemoryTableCatalog? _catalogOverride;

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
    internal static InMemoryTableCatalog CatalogFor(TargetSqlDialect dialect)
    {
        if (dialect == TargetSqlDialect.SqlServer) return Catalog();
        if (dialect == TargetSqlDialect.Oracle)
        {
            return new InMemoryTableCatalog(new[]
            {
                new TableCatalogEntry(Orders, System.Collections.Immutable.ImmutableArray.Create(
                    new CatalogColumn("Id", "NUMBER(10)"), new CatalogColumn("TenantId", "VARCHAR2(64)"), new CatalogColumn("Region", "VARCHAR2(20)"),
                    new CatalogColumn("Status", "VARCHAR2(20)"), new CatalogColumn("Amount", "NUMBER(18,2)"), new CatalogColumn("Email", "VARCHAR2(200)")),
                    "TenantId", 1),
                new TableCatalogEntry(Entitlements, System.Collections.Immutable.ImmutableArray.Create(
                    new CatalogColumn("Id", "NUMBER(10)"), new CatalogColumn("TenantId", "VARCHAR2(64)"), new CatalogColumn("OrderId", "NUMBER(10)")),
                    "TenantId", 1),
                new TableCatalogEntry(Lookup, System.Collections.Immutable.ImmutableArray.Create(new CatalogColumn("Code", "VARCHAR2(10)")), null, 1)
            }, "dbo");
        }

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
        AllowExperimentalDialect = true,   // Databricks stays Experimental until the first green G9 run (CR-ADG-03)
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = statements,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = _catalogOverride ?? CatalogFor(dialect),
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
    public void Insert_TenantColumnMissing_IsRejected_AndTheRequirementCannotBeRelaxedPerRequest(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "INSERT INTO orders (id, status) VALUES (1, 'open')");
        var relaxed = DmlGuardOptions.Strict with { RequireTenantColumnInInsert = false };
        Assert.Throws<SqlCompileConfigurationException>(() => Compile(dialect, "INSERT INTO orders (id, status) VALUES (1, 'open')", dml: relaxed));
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
    public void Insert_IntoATableWithARowPolicy_CompilesWithTheCheckOption_AndExpectsOneRowPerValuesRow(TargetSqlDialect dialect)
    {
        if (dialect == TargetSqlDialect.Databricks) return;   // no INSERT row count, see the Delta test below
        RegionPolicy();
        var one = Compile(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')");
        Assert.True(one.RequiresRowCountCheck);
        Assert.Equal(1, one.ExpectedAffectedRows);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 1), one.AppliedPredicates);
        Assert.Contains(one.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        Assert.Contains(one.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Contains("WHERE", one.Sql);
        Assert.Contains("autheris_ins", one.Sql);

        var three = Compile(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU'), (2, 'acme', 'US'), (3, 'acme', 'EU')");
        Assert.True(three.RequiresRowCountCheck);
        Assert.Equal(3, three.ExpectedAffectedRows);
        Assert.Equal(2, CountOf(three.Sql, "UNION ALL"));
    }

    // ---- CR-ADG-42: the check runs on the stored value (cast to the catalog type) and compares strings byte-exact ----

    private static ColumnReference OrdersColumn(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static PolicyParameterExpression PolicyParam(string name, SqlParameterType type = SqlParameterType.String) => new(name, type);

    private void Policy(Expression expression, params (string Name, object Value, SqlParameterType Type)[] values)
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(expression,
            values.ToDictionary(v => v.Name, v => new PolicyValue(v.Value, v.Type)));
    }

    private void AmountBelowPolicy() =>
        Policy(new BinaryExpression(OrdersColumn("Amount"), BinaryOperator.LessThan, PolicyParam("__pol_amount", SqlParameterType.Decimal)),
            ("__pol_amount", 100m, SqlParameterType.Decimal));

    private void WithOrdersColumnType(TargetSqlDialect dialect, string column, string type)
    {
        var entry = CatalogFor(dialect).Resolve(new SqlQualifiedName(new[] { new SqlIdentifier("dbo"), new SqlIdentifier("Orders") }))!;
        _catalogOverride = new InMemoryTableCatalog(new[]
        {
            entry with { Columns = entry.Columns.Select(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase) ? c with { DataType = type } : c).ToImmutableArray() }
        }, "dbo");
    }

    private static string ByteExactMarker(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => "DATALENGTH",
        TargetSqlDialect.PostgreSql => "TEXTSEND",
        TargetSqlDialect.Oracle => "CAST_TO_RAW",
        _ => "ENCODE("
    };

    public static IEnumerable<object[]> CheckDialectData() => Dialects.Where(d => d != TargetSqlDialect.Databricks).Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(CheckDialectData))]
    public void Insert_CheckOption_CastsEveryWrittenValueToItsCatalogType_ButTheBoundTenant(TargetSqlDialect dialect)
    {
        AmountBelowPolicy();
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid, region, amount) VALUES (1, 'acme', 'EU', 99.999)");
        string[] expected = dialect switch
        {
            TargetSqlDialect.SqlServer => new[] { "CAST(", "AS int)", "AS nvarchar(20))", "AS decimal(18,2))" },
            TargetSqlDialect.Oracle => new[] { "CAST(", "AS NUMBER(10))", "AS VARCHAR2(20))", "AS NUMBER(18,2))" },
            TargetSqlDialect.PostgreSql => new[] { "CAST(", "AS integer)", "AS varchar)", "AS decimal(18,2))" },
            _ => new[] { "CAST(", "AS integer)", "AS varchar)", "AS decimal(18,2))" }
        };
        foreach (var part in expected) Assert.Contains(part, c.Sql);
        // the policy is evaluated over the derived table (the cast values), and so is the projection that is inserted
        Assert.Contains("autheris_ins", c.Sql);
        Assert.True(c.RequiresRowCountCheck);
        // a numeric policy needs no byte-exact string comparison
        Assert.DoesNotContain(ByteExactMarker(dialect), c.Sql.ToUpperInvariant());
    }

    [Theory]
    [MemberData(nameof(CheckDialectData))]
    public void Insert_CheckOption_WithAnUnknownOrUnsupportedCatalogType_IsRejectedWithATypedError(TargetSqlDialect dialect)
    {
        RegionPolicy();
        foreach (string type in new[] { "geography", "xml", "", "   ", "varchar(abc)", "citext", "nvarchar(20); DROP TABLE x" })
        {
            WithOrdersColumnType(dialect, "Region", type);
            var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')"));
            Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
            Assert.DoesNotContain("DROP", ex.Message);
        }

        // a column the policy does not reference needs a known type, too: the stored row is what the check proves
        _catalogOverride = null;
        WithOrdersColumnType(dialect, "Status", "geography");
        Assert.Throws<SqlCompileNotSupportedException>(() => Compile(dialect, "INSERT INTO orders (id, tenantid, region, status) VALUES (1, 'acme', 'EU', 's')"));
    }

    [Theory]
    [MemberData(nameof(CheckDialectData))]
    public void Insert_CheckOption_StringEquality_IsByteExact(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'eu')");
        Assert.True(c.Sql.ToUpperInvariant().Contains(ByteExactMarker(dialect), StringComparison.Ordinal), c.Sql);
    }

    [Theory]
    [MemberData(nameof(CheckDialectData))]
    public void Insert_CheckOption_StringIn_IsByteExactPerValue_AndMixesWithOtherColumns(TargetSqlDialect dialect)
    {
        Policy(new BinaryExpression(
                new InListExpression(OrdersColumn("Region"), new Expression[] { PolicyParam("__pol_a"), PolicyParam("__pol_b") }, false),
                BinaryOperator.And,
                new BinaryExpression(OrdersColumn("Amount"), BinaryOperator.LessThan, PolicyParam("__pol_amount", SqlParameterType.Decimal))),
            ("__pol_a", "EU", SqlParameterType.String), ("__pol_b", "US", SqlParameterType.String), ("__pol_amount", 100m, SqlParameterType.Decimal));
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid, region, amount) VALUES (1, 'acme', 'EU', 5)");
        Assert.True(CountOf(c.Sql.ToUpperInvariant(), ByteExactMarker(dialect)) >= 2);
    }

    public static IEnumerable<object[]> StringShapes()
    {
        foreach (var dialect in Dialects.Where(d => d != TargetSqlDialect.Databricks))
        {
            foreach (string shape in new[] { "less", "notequal", "like", "not", "notin", "function", "between", "columns" })
            {
                yield return new object[] { dialect, shape };
            }
        }
    }

    [Theory]
    [MemberData(nameof(StringShapes))]
    public void Insert_CheckOption_StringPredicateOtherThanEqualityOrIn_IsRejectedWithATypedError(TargetSqlDialect dialect, string shape)
    {
        var region = OrdersColumn("Region");
        var p = PolicyParam("__pol_region");
        Expression expression = shape switch
        {
            "less" => new BinaryExpression(region, BinaryOperator.LessThan, p),
            "notequal" => new BinaryExpression(region, BinaryOperator.NotEqual, p),
            "like" => new LikeExpression(region, p),
            "not" => new UnaryExpression(UnaryOperator.Not, new ParenthesizedExpression(new BinaryExpression(region, BinaryOperator.Equal, p))),
            "notin" => new InListExpression(region, new Expression[] { p }, true),
            "function" => new BinaryExpression(new FunctionCallExpression(new SqlQualifiedName("lower"), new Expression[] { region }), BinaryOperator.Equal, p),
            "between" => new BetweenExpression(region, p, p, false),
            _ => new BinaryExpression(region, BinaryOperator.Equal, OrdersColumn("Status"))
        };
        Policy(expression, ("__pol_region", "EU", SqlParameterType.String));
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(dialect, "INSERT INTO orders (id, tenantid, region, status) VALUES (1, 'acme', 'EU', 's')"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
        Assert.DoesNotContain("EU", ex.Message);
    }

    [Theory]
    [MemberData(nameof(CheckDialectData))]
    public void Insert_CheckOption_NullTestOnAStringColumn_IsAllowed(TargetSqlDialect dialect)
    {
        Policy(new UnaryExpression(UnaryOperator.IsNotNull, OrdersColumn("Region")));
        Assert.True(Compile(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')").RequiresRowCountCheck);
    }

    // CR-ADG-44: the row set is a balanced tree; 1,000 rows do not reach the AST depth limit
    [Theory]
    [MemberData(nameof(CheckDialectData))]
    public void Insert_CheckOption_ThousandRows_StayWithinTheLimits(TargetSqlDialect dialect)
    {
        RegionPolicy();
        string values = string.Join(", ", Enumerable.Range(1, 1000).Select(i => $"({i}, 'acme', 'EU')"));
        var request = Request(dialect);
        try
        {
            var c = _engine.Compile($"INSERT INTO orders (id, tenantid, region) VALUES {values}".AsMemory(), request, CancellationToken.None);
            Assert.Equal(1000, c.ExpectedAffectedRows);
            Assert.Equal(999, CountOf(c.Sql, "UNION ALL"));
        }
        catch (SqlLimitExceededException ex) when (dialect == TargetSqlDialect.SqlServer)
        {
            // SQL Server (2,100 parameters) cannot bind 2,000 values plus the policy and tenant binds of this shape: the typed limit error, never an AST depth error
            Assert.Equal(SqlLimitKind.BindParameters, ex.Kind);
        }
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_IntoATableWithoutARowPolicy_NeedsNoRowCountCheck(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'acme')");
        Assert.False(c.RequiresRowCountCheck);
        Assert.Null(c.ExpectedAffectedRows);
        // a user SELECT over a derived table is never mistaken for the check-option shape
        var viaSelect = Compile(dialect, "INSERT INTO orders (id, tenantid) SELECT d.orderid, 'acme' FROM (SELECT orderid FROM entitlements) d WHERE d.orderid > 1");
        Assert.False(viaSelect.RequiresRowCountCheck);
    }

    [Fact]
    public void Insert_CheckOption_OnDelta_IsRejectedWithATypedError_BecauseNoRowCountIsReported()
    {
        RegionPolicy();
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() =>
            Compile(TargetSqlDialect.Databricks, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
        Assert.Contains("row count", ex.Message);
        Assert.False(DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks).ReportsInsertRowCount);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_IntoAPolicyTable_ConsentCorrelatedSelectOrMissingPolicyColumn_StaysRejected(TargetSqlDialect dialect)
    {
        if (dialect == TargetSqlDialect.Databricks) return;
        RegionPolicy();
        // INSERT ... SELECT has no countable source
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, region) SELECT id, 'acme', 'EU' FROM entitlements");
        // the policy column is not supplied, so the check cannot be evaluated
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme')");
        // a consent-based policy
        var consent = Request(dialect);
        consent = consent with { Policy = consent.Policy with { TablesWithConsentRowFilter = new HashSet<string> { "Orders" } } };
        Assert.ThrowsAny<SecurityException>(() => _engine.Compile("INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')".AsMemory(), consent, CancellationToken.None));
        // a correlated policy
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("Id", true) })),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_id", SqlParameterType.Int64)),
            new Dictionary<string, PolicyValue> { ["__pol_id"] = new(1L, SqlParameterType.Int64) });
        RejectedSecurity(dialect, "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')");
        // MERGE INSERT into a policy table stays rejected
        RegionPolicy();
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED THEN INSERT (id, tenantid, region) VALUES (s.id, 'acme', 'EU')");
    }

    [Fact]
    public void TheRowCountContract_RollsBackOnAnyDifference_AndSurvivesTheCache()
    {
        RegionPolicy();
        const string sql = "INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU'), (2, 'acme', 'US')";
        var first = Compile(TargetSqlDialect.SqlServer, sql);
        var cached = Compile(TargetSqlDialect.SqlServer, sql);
        Assert.Equal(1, _engine.CompileCache.Stats.Hits);
        Assert.Equal(2, cached.ExpectedAffectedRows);
        DmlCheckOption.Enforce(first, 2);   // all rows pass: no exception
        foreach (int affected in new[] { 0, 1, 3 })
        {
            var ex = Assert.Throws<DmlCheckOptionViolationException>(() => DmlCheckOption.Enforce(cached, affected));
            Assert.Equal(GovernedSqlErrorCodes.CheckOptionViolation, ex.Code);
            Assert.Equal("DML_CHECK_OPTION_VIOLATION", ex.Code);
            Assert.Null(ex.InnerException);
            Assert.DoesNotContain("EU", ex.ToString());
        }

        // statements without the check are never affected
        DmlCheckOption.Enforce(Compile(TargetSqlDialect.SqlServer, "DELETE FROM orders WHERE id = 1"), 99);
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

    // CR-ADG-37: more tautology shapes; the filter is a safety net (the tenant predicate is always ANDed), but it must not be trivial to defeat.
    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_WithFurtherTautologies_IsRejected_AsUnfilteredDml(TargetSqlDialect dialect)
    {
        foreach (var where in new[]
                 {
                     "id IS NOT NULL OR id IS NULL", "id IS NULL OR id IS NOT NULL", "(id IS NOT NULL) OR (id IS NULL)",
                     "NOT (false)", "NOT (1 = 0)", "NOT (NOT (true))", "1 = 1 OR id = 5", "id = 5 OR (1 = 1)",
                     "(id IS NOT NULL OR id IS NULL) AND true"
                 })
        {
            Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "UPDATE orders SET status = 'x' WHERE " + where));
        }

        // a genuine filter is untouched
        Compile(dialect, "UPDATE orders SET status = 'x' WHERE id IS NOT NULL AND status IS NULL");
        Compile(dialect, "UPDATE orders SET status = 'x' WHERE id IS NOT NULL OR status IS NULL");
        Compile(dialect, "UPDATE orders SET status = 'x' WHERE NOT (id = 5)");
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

    // CR-ADG-38: a security guard cannot be relaxed per request on the typed path; there is no silent no-op and no weaker mode.
    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_PolicyColumnAssignment_StaysRejected_WhenTheGuardIsSwitchedOff_AsAConfigurationError(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var relaxed = DmlGuardOptions.Strict with { RejectPolicyColumnAssignment = false };
        Assert.Throws<SqlCompileConfigurationException>(() => Compile(dialect, "UPDATE orders SET region = 'US' WHERE id = 7", dml: relaxed));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void EveryRelaxedDmlGuardSwitch_IsRejected_WithATypedConfigurationError_ForAnyStatement(TargetSqlDialect dialect)
    {
        var switches = typeof(DmlGuardOptions).GetProperties().Where(p => p.PropertyType == typeof(bool)).ToList();
        Assert.Equal(9, switches.Count);   // a new switch must be added to the strict check, not skipped
        foreach (var guard in switches)
        {
            var relaxed = DmlGuardOptions.Strict with { };
            typeof(DmlGuardOptions).GetProperty(guard.Name)!.SetValue(relaxed, false);
            Assert.NotEqual(DmlGuardOptions.Strict, relaxed);
            var ex = Assert.Throws<SqlCompileConfigurationException>(() => Compile(dialect, "SELECT id FROM orders", dml: relaxed));
            Assert.DoesNotContain(guard.Name, ex.Message);
            Assert.Throws<SqlCompileConfigurationException>(() => Compile(dialect, "UPDATE orders SET status = 'x' WHERE id = 1", dml: relaxed));
        }
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void EnforceWithCheckOptionFalse_IsRejected_NotSilentlyIgnored(TargetSqlDialect dialect) =>
        Assert.Throws<SqlCompileConfigurationException>(() =>
            Compile(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme')", dml: DmlGuardOptions.Strict with { EnforceWithCheckOption = false }));

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
        // Databricks refuses a subquery in the condition (CR-ADG-39): its WHERE stays plain
        string where = dialect == TargetSqlDialect.Databricks ? "id = 1" : "id IN (SELECT orderid FROM entitlements WHERE id > 3)";
        var c = Compile(dialect, "UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE " + where);
        Assert.Equal(1, Tenants(c));
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
        Assert.True(CountOf(c.Sql, "[Entitlements]", "\"Entitlements\"", "`Entitlements`") >= (dialect == TargetSqlDialect.Databricks ? 1 : 2));
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
        if (dialect == TargetSqlDialect.Databricks) return;   // CR-ADG-39: rejected at compile time, see the next test
        var c = Compile(dialect, "DELETE FROM orders WHERE id IN (SELECT orderid FROM entitlements)");
        Assert.Contains(new SecurityPredicateId("dbo.Entitlements", 0), c.AppliedPredicates);
        Assert.Equal(1, Tenants(c));
    }

    // CR-ADG-39: Delta refuses a subquery in the condition of UPDATE and DELETE at analysis; the compiler says so with a typed error.
    [Fact]
    public void Databricks_SubqueryInUpdateOrDeleteCondition_IsRejectedAtCompileTime_WithATypedError()
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM orders WHERE id IN (SELECT orderid FROM entitlements)",
                     "DELETE FROM orders WHERE EXISTS (SELECT 1 FROM entitlements e WHERE e.orderid = 1)",
                     "UPDATE orders SET status = 'x' WHERE id = (SELECT max(orderid) FROM entitlements)",
                     "UPDATE orders SET status = 'x' WHERE id > ANY (SELECT orderid FROM entitlements)"
                 })
        {
            var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(TargetSqlDialect.Databricks, sql));
            Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
            Assert.DoesNotContain("entitlements", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // plain conditions and every other dialect are unaffected
        Compile(TargetSqlDialect.Databricks, "DELETE FROM orders WHERE id = 1");
        Assert.False(DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks).SupportsSubqueryInDmlCondition);
        foreach (var dialect in Dialects.Where(d => d != TargetSqlDialect.Databricks))
        {
            Assert.True(DialectCapabilityTable.Default.Get(dialect).SupportsSubqueryInDmlCondition);
            Compile(dialect, "DELETE FROM orders WHERE id IN (SELECT orderid FROM entitlements)");
        }
    }

    // CR-ADG-36: the injected mask of a secured subquery is not a user read of the target's masked column.
    [Theory]
    [MemberData(nameof(DialectData))]
    public void Update_SubqueryOverAMaskedTable_ThatDoesNotReadTheMaskedColumn_Compiles(TargetSqlDialect dialect)
    {
        MaskEmail();
        var c = Compile(dialect, "UPDATE orders SET status = (SELECT max(o2.status) FROM orders o2 WHERE o2.id = 1) WHERE id = 1");
        Assert.Equal(SqlStatementClass.Update, c.StatementClass);
        // reading the masked column of the target stays rejected, directly and through the subquery
        RejectedSecurity(dialect, "UPDATE orders SET status = email WHERE id = 1");
        RejectedSecurity(dialect, "UPDATE orders SET status = (SELECT max(o2.status) FROM orders o2 WHERE o2.id = 1) WHERE email = 'x'");
    }

    // ---- MERGE ----

    /// <summary>Oracle has neither a stand-alone MERGE DELETE nor repeated clause kinds (see <see cref="MergeClauseShape"/>).</summary>
    private static string MergeUpdateDelete(TargetSqlDialect dialect) =>
        "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
        (dialect == TargetSqlDialect.Oracle
            ? "WHEN MATCHED AND s.id > 1 THEN UPDATE SET status = 'merged'"
            : "WHEN MATCHED AND s.id > 1 THEN UPDATE SET status = 'merged' WHEN MATCHED THEN DELETE");

    /// <summary>A MERGE clause that every dialect can express (used by tests whose subject is not the DELETE clause).</summary>
    private const string Matched = "WHEN MATCHED THEN UPDATE SET status = 'x'";

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_TargetPredicatesGoIntoOn_AndTheSourceIsSecured(TargetSqlDialect dialect)
    {
        RegionPolicy();
        var c = Compile(dialect, MergeUpdateDelete(dialect));
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
    public void OracleMerge_DeleteClause_AndRepeatedClauseKinds_AreRejectedFailClosed_WithATypedError()
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(TargetSqlDialect.Oracle,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN DELETE"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
        ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(TargetSqlDialect.Oracle,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN MATCHED AND s.id = 1 THEN UPDATE SET status = 'a' WHEN MATCHED AND s.id = 2 THEN UPDATE SET status = 'b'"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
        ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(TargetSqlDialect.Oracle,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN NOT MATCHED AND s.id = 1 THEN INSERT (id, tenantid) VALUES (s.id, 'acme') WHEN NOT MATCHED THEN INSERT (id, tenantid) VALUES (s.id, 'acme')"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    [Fact]
    public void OracleMerge_ClauseConditionsAreWhere_TheOnIsParenthesized_AndAliasesHaveNoAs()
    {
        var c = Compile(TargetSqlDialect.Oracle,
            "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
            "WHEN MATCHED AND s.id > 1 THEN UPDATE SET status = 'm' WHEN NOT MATCHED AND s.id > 2 THEN INSERT (id, tenantid) VALUES (s.id, 'acme')");
        Assert.Matches(@"^MERGE INTO ""dbo""\.""Orders"" \S+ USING \(", c.Sql);
        Assert.DoesNotContain(" AS \"t\"", c.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" ON (", c.Sql);
        Assert.DoesNotContain("WHEN MATCHED AND", c.Sql);
        Assert.DoesNotContain("WHEN NOT MATCHED AND", c.Sql);
        Assert.Matches(@"THEN UPDATE SET .* WHERE ", c.Sql);
        Assert.Matches(@"THEN INSERT .* VALUES .* WHERE ", c.Sql);
        Assert.DoesNotContain(";", c.Sql);
    }

    [Fact]
    public void Merge_SqlServer_EndsWithExactlyOneSemicolon_AtTheFinalPosition()
    {
        var c = Compile(TargetSqlDialect.SqlServer, MergeUpdateDelete(TargetSqlDialect.SqlServer));
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

    // CR-ADG-33: the source qualifier must differ from the target qualifier and the target table name, case-insensitively.
    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_SourceAliasEqualToTheTargetAlias_IsRejected_InAnyCase(TargetSqlDialect dialect)
    {
        const string tail = " ON t.id = 1 WHEN MATCHED THEN UPDATE SET status = 'x'";
        RejectedSecurity(dialect, "MERGE INTO orders t USING (SELECT id FROM entitlements) t" + tail);
        RejectedSecurity(dialect, "MERGE INTO orders t USING (SELECT id FROM entitlements) T" + tail);
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements t" + tail);
        RejectedSecurity(dialect, "MERGE INTO orders USING (SELECT id FROM entitlements) orders ON orders.id = 1 WHEN MATCHED THEN UPDATE SET status = 'x'");
        RejectedSecurity(dialect, "MERGE INTO orders USING (SELECT id FROM entitlements) ORDERS ON orders.id = 1 WHEN MATCHED THEN UPDATE SET status = 'x'");
        RejectedSecurity(dialect, "MERGE INTO orders t USING (SELECT id FROM entitlements) orders" + tail);
        // a distinct alias still compiles
        Assert.Equal(SqlStatementClass.Merge, Compile(dialect, "MERGE INTO orders t USING (SELECT id FROM entitlements) s ON t.id = s.id WHEN MATCHED THEN UPDATE SET status = 'x'").StatementClass);
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
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.email = 'x' WHEN MATCHED THEN UPDATE SET status = 'x'");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED AND t.email = 'x' THEN UPDATE SET status = 'x'");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = t.email");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = email");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED AND t.email IS NULL THEN INSERT (id, tenantid) VALUES (s.id, 'acme')");
        RejectedSecurity(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, 'acme', t.email)");
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_TriviallyTrueOrColumnFreeOn_IsRejected_AsUnfilteredDml(TargetSqlDialect dialect)
    {
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "MERGE INTO orders t USING entitlements s ON 1 = 1 WHEN MATCHED THEN UPDATE SET status = 'x'"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "MERGE INTO orders t USING entitlements s ON true WHEN MATCHED THEN UPDATE SET status = 'x'"));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid OR 1 = 1 WHEN MATCHED THEN UPDATE SET status = 'x'"));
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
        RejectedSecurity(dialect, MergeUpdateDelete(dialect));
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
        Assert.True(CountOf(joined.Sql, "[Orders]", "\"Orders\"", "`Orders`") >= 2);   // target and source are both the physical table
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_TargetWithoutAlias_UsesTheTableNameAsTheQualifier(TargetSqlDialect dialect)
    {
        var c = Compile(dialect, "MERGE INTO orders USING entitlements s ON orders.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'");
        Assert.Equal(SqlStatementClass.Merge, c.StatementClass);
        Assert.Equal(1, Tenants(c));
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Merge_UnknownTarget_AndTableBranch_AreRejected(TargetSqlDialect dialect)
    {
        RejectedSecurity(dialect, "MERGE INTO mystery t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'");
        Rejected(dialect, "MERGE INTO orders@main t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'");
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
    public void ARelaxedDmlGuard_IsRejected_AndNeverCached_AStrictRequestIsUnaffected(TargetSqlDialect dialect)
    {
        var relaxed = DmlGuardOptions.Strict with { RejectUnfilteredDml = false };
        Assert.Throws<SqlCompileConfigurationException>(() => Compile(dialect, "UPDATE orders SET status = 'x'", dml: relaxed));
        Assert.Throws<UnfilteredDmlException>(() => Compile(dialect, "UPDATE orders SET status = 'x'"));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
        Assert.Equal(0, _engine.CompileCache.Stats.Hits);
    }

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Dml_IsNeverRowLimited_ByEnforcedMaxRows(TargetSqlDialect dialect)
    {
        // The enforced row limit is for the root SELECT of a read; truncating the source of a write would change which rows are written.
        var request = Request(dialect) with { EnforcedMaxRows = 10 };
        foreach (var sql in new[]
                 {
                     "INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM entitlements",
                     dialect == TargetSqlDialect.Databricks
                         ? "UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE id = 1"
                         : "UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE id IN (SELECT orderid FROM entitlements)",
                     dialect == TargetSqlDialect.Databricks ? "DELETE FROM orders WHERE id = 1" : "DELETE FROM orders WHERE id IN (SELECT orderid FROM entitlements)"
                 })
        {
            var c = _engine.Compile(sql.AsMemory(), request, CancellationToken.None);
            Assert.DoesNotContain("FETCH", c.Sql);
            Assert.DoesNotContain("LIMIT", c.Sql);
            Assert.DoesNotContain("ROWNUM", c.Sql);
        }
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
