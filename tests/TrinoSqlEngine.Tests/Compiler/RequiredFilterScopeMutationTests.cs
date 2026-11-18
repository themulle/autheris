using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using ReqVisitor = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the required-filter check of the security visitor: which predicate shapes count as constraining a
/// required filter column (constant comparisons, BETWEEN, IN, conjunction, disjunction), which spellings of the column belong to the
/// secured table in a multi-source scope (alias, schema-qualified name, ambiguity with other sources), and how groups, tenant and row
/// policy satisfy the requirement. An unconstrained column is rejected with <see cref="RequiredFilterColumnMissingException"/> (fail closed).
/// </summary>
[Trait("Category", "SqlGate")]
public class RequiredFilterScopeMutationTests
{
    private static readonly TableIdentity Orders = new("dbo", "orders");
    private static readonly TableIdentity Items = new("dbo", "items");
    private static readonly TableIdentity Refs = new("dbo", "refs");
    private static readonly TableIdentity Grp = new("dbo", "grp");

    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();

    public RequiredFilterScopeMutationTests()
    {
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Items);
        _rowFilters.NoPolicy.Add(Refs);
        _rowFilters.NoPolicy.Add(Grp);
    }

    private static ImmutableArray<CatalogColumn> Cols(params string[] names) =>
        names.Select(n => new CatalogColumn(n, n is "id" or "amount" or "qty" or "order_id" ? "integer" : "varchar")).ToImmutableArray();

    private static InMemoryTableCatalog Catalog(bool ordersRequireTenant = false) => new(new[]
    {
        new TableCatalogEntry(Orders, Cols("id", "tenant_id", "region", "status", "amount"), "tenant_id",
            ordersRequireTenant ? ImmutableArray.Create("tenant_id") : ImmutableArray.Create("region"), 1),
        new TableCatalogEntry(Items, Cols("id", "order_id", "region", "qty"), null, ImmutableArray<string>.Empty, 1),
        new TableCatalogEntry(Refs, Cols("id", "name"), null, ImmutableArray<string>.Empty, 1),
        new TableCatalogEntry(Grp, Cols("id", "a", "b", "c"), null, ImmutableArray<string>.Empty, 1,
            ImmutableArray.Create(ImmutableArray.Create("a"), ImmutableArray<string>.Empty, ImmutableArray.Create("b", "c")))
    }, "dbo");

    private CompiledSql Compile(string sql, InMemoryTableCatalog? catalog = null, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) =>
        _engine.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = catalog ?? Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }.WithTestWritePermit(sql), CancellationToken.None);

    private static IEnumerable<object[]> Rows(params string[] items) => items.Select(i => new object[] { i });

    public static IEnumerable<object[]> Constraining() => Rows(
        "SELECT id FROM orders WHERE region = 'EU'", "SELECT id FROM orders WHERE 'EU' = region", "SELECT id FROM orders WHERE region < 'Z'",
        "SELECT id FROM orders WHERE 'Z' > region", "SELECT id FROM orders WHERE region <= 'Z'", "SELECT id FROM orders WHERE 'Z' >= region",
        "SELECT id FROM orders WHERE region > 'A'", "SELECT id FROM orders WHERE 'A' < region", "SELECT id FROM orders WHERE region >= 'A'",
        "SELECT id FROM orders WHERE 'A' <= region", "SELECT id FROM orders WHERE (region = 'EU')", "SELECT id FROM orders WHERE ((region = 'EU'))",
        "SELECT id FROM orders WHERE region = CAST('EU' AS varchar)",
        "SELECT id FROM orders WHERE region BETWEEN 'A' AND 'F'", "SELECT id FROM orders WHERE region IN ('A', 'B')",
        "SELECT id FROM orders WHERE region IN ('A', NULL)", "SELECT id FROM orders WHERE region IN (SELECT region FROM items)",
        "SELECT id FROM orders WHERE region = 'EU' AND status = 'x'", "SELECT id FROM orders WHERE status = 'x' AND region = 'EU'",
        "SELECT id FROM orders WHERE status = 'x' AND amount > 1 AND region = 'EU'", "SELECT id FROM orders WHERE region = 'EU' AND status = 'x' AND amount > 1",
        "SELECT id FROM orders WHERE status = 'x' AND region = 'EU' AND amount > 1", "SELECT id FROM orders WHERE (status = 'x' AND region = 'EU') AND amount > 1",
        "SELECT id FROM orders WHERE region = 'EU' OR region = 'US'", "SELECT id FROM orders WHERE region = 'EU' OR region = 'US' OR region = 'DE'",
        "SELECT id FROM orders WHERE (region = 'EU' OR region = 'US') AND status = 'x'", "SELECT id FROM orders WHERE region = 'EU' OR (region > 'M' AND status = 'x')",
        "SELECT id FROM orders o WHERE o.region = 'EU'", "SELECT id FROM orders o WHERE region = 'EU'", "SELECT id FROM orders WHERE orders.region = 'EU'",
        "SELECT id FROM orders WHERE ORDERS.REGION = 'EU'", "SELECT id FROM orders WHERE REGION = 'EU'",
        "SELECT id FROM dbo.orders WHERE dbo.orders.region = 'EU'", "SELECT id FROM dbo.orders WHERE orders.region = 'EU'",
        "SELECT o.id FROM orders o JOIN refs r ON o.id = r.id WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id WHERE o.region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id AND o.region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON i.order_id = o.id AND (o.region = 'EU' OR o.region = 'US')",
        "SELECT o.id FROM items i JOIN orders o ON o.id = i.order_id WHERE i.region = 'EU' AND o.region = 'EU'",
        "SELECT t.id FROM (SELECT id, region FROM orders WHERE region = 'EU') t",
        "SELECT o.id FROM orders o JOIN (SELECT id AS rid FROM refs) r ON o.id = r.rid WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT id AS rid, region AS rr FROM items) r ON o.id = r.rid WHERE o.region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT id, name FROM refs) AS r (rid, rname) ON o.id = r.rid WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT id FROM refs UNION ALL SELECT id FROM refs) r ON o.id = r.id WHERE region = 'EU'",
        "WITH x AS (SELECT id FROM refs) SELECT o.id FROM orders o JOIN x ON o.id = x.id WHERE o.region = 'EU'");

    public static IEnumerable<object[]> NotConstraining() => Rows(
        "SELECT id FROM orders", "SELECT id FROM orders WHERE status = 'x'", "SELECT id FROM orders WHERE region = status", "SELECT id FROM orders WHERE status = region",
        "SELECT id FROM orders WHERE region = NULL", "SELECT id FROM orders WHERE NULL = region", "SELECT id FROM orders WHERE region = CAST(NULL AS varchar)",
        "SELECT id FROM orders WHERE region < status", "SELECT id FROM orders WHERE status > region", "SELECT id FROM orders WHERE region <= NULL",
        "SELECT id FROM orders WHERE region >= NULL", "SELECT id FROM orders WHERE region > NULL", "SELECT id FROM orders WHERE region < NULL",
        "SELECT id FROM orders WHERE region <> 'EU'", "SELECT id FROM orders WHERE region IS NULL", "SELECT id FROM orders WHERE region IS NOT NULL",
        "SELECT id FROM orders WHERE region LIKE 'E%'", "SELECT id FROM orders WHERE lower(region) = 'eu'", "SELECT id FROM orders WHERE region || 'x' = 'EUx'",
        "SELECT id FROM orders WHERE region NOT BETWEEN 'A' AND 'F'", "SELECT id FROM orders WHERE region BETWEEN status AND 'F'",
        "SELECT id FROM orders WHERE region BETWEEN 'A' AND status", "SELECT id FROM orders WHERE region BETWEEN NULL AND 'F'",
        "SELECT id FROM orders WHERE region BETWEEN 'A' AND NULL", "SELECT id FROM orders WHERE status BETWEEN 'A' AND 'F'",
        "SELECT id FROM orders WHERE region NOT IN ('A', 'B')", "SELECT id FROM orders WHERE region IN (NULL)", "SELECT id FROM orders WHERE region IN ('A', status)",
        "SELECT id FROM orders WHERE region IN (status, 'A')", "SELECT id FROM orders WHERE status IN ('A', 'B')",
        "SELECT id FROM orders WHERE region NOT IN (SELECT region FROM items)", "SELECT id FROM orders WHERE status IN (SELECT region FROM items)",
        "SELECT id FROM orders WHERE region = 'EU' OR status = 'x'", "SELECT id FROM orders WHERE status = 'x' OR region = 'EU'",
        "SELECT id FROM orders WHERE region = 'EU' OR region = status", "SELECT id FROM orders WHERE region = status OR region = 'EU'",
        "SELECT id FROM orders WHERE region = 'EU' OR region = 'US' OR status = 'x'", "SELECT id FROM orders WHERE region = 'EU' OR status = 'x' OR region = 'US'",
        "SELECT id FROM orders WHERE status = 'x' OR region = 'EU' OR region = 'US'", "SELECT id FROM orders WHERE NOT region = 'EU'",
        "SELECT id FROM orders WHERE NOT (region = 'EU')",         "SELECT id FROM orders o WHERE region = 'EU' OR o.status = 'x'",
        "SELECT id FROM orders o WHERE x.region = 'EU'", "SELECT id FROM orders o WHERE orders.region = 'EU'", "SELECT id FROM orders o WHERE dbo.orders.region = 'EU'",
        "SELECT id FROM orders WHERE other.orders.region = 'EU'", "SELECT id FROM orders WHERE dbo.other.region = 'EU'", "SELECT id FROM orders WHERE x.dbo.orders.region = 'EU'",
        "SELECT id FROM orders WHERE x.region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id WHERE i.region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id WHERE o.region = i.region",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id AND i.region = 'EU'",
        "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id",
        "SELECT o.id FROM orders o JOIN orders o2 ON o.id = o2.id WHERE o.region = 'EU'",
        "SELECT o.id FROM orders o JOIN orders o2 ON o.id = o2.id WHERE o.region = 'EU' AND o2.status = 'x'",
        "SELECT o.id FROM orders o JOIN orders o2 ON o.id = o2.id WHERE region = 'EU'",
        "SELECT orders.id FROM orders JOIN items orders ON orders.id = items.order_id WHERE orders.region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT * FROM refs) r ON o.id = r.id WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT id, region FROM items) r ON o.id = r.id WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT id, name FROM refs) AS r (rid, region) ON o.id = r.rid WHERE region = 'EU'",
        "SELECT o.id FROM orders o JOIN (SELECT id, region FROM items UNION ALL SELECT id, region FROM items) r ON o.id = r.id WHERE region = 'EU'",
        "WITH x AS (SELECT id FROM refs) SELECT o.id FROM orders o JOIN x ON o.id = x.id WHERE region = 'EU'",
        "SELECT id FROM orders WHERE dbo.orders.region = 'EU'",
        "SELECT t.id FROM (SELECT id, region FROM orders) t WHERE t.region = 'EU'");

    [Fact]
    public void Cast_of_the_column_satisfies_the_required_filter_on_a_table_without_tenant_column()
    {
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, Cols("id", "region"), null, ImmutableArray.Create("region"), 1)
        }, "dbo");

        Assert.False(string.IsNullOrEmpty(Compile("SELECT id FROM orders WHERE CAST(region AS varchar) = 'EU'", catalog).Sql));
    }

    [Theory]
    [MemberData(nameof(Constraining))]
    public void Constraint_SatisfiesTheRequiredFilter(string sql)
    {
        var compiled = Compile(sql);
        Assert.False(string.IsNullOrEmpty(compiled.Sql));
        Assert.Contains("tenant_id", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(NotConstraining))]
    public void NoConstraint_IsRejectedWithTheRequiredFilterCode(string sql)
    {
        var ex = Assert.ThrowsAny<GovernedSqlException>(() => Compile(sql));
        Assert.IsType<RequiredFilterColumnMissingException>(ex);
        Assert.Equal(GovernedSqlErrorCodes.RequiredFilterColumnMissing, ex.Code);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    public void Rejection_CarriesTheCompileDialect(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<RequiredFilterColumnMissingException>(() => Compile("SELECT id FROM orders", null, dialect));
        Assert.Equal(dialect, ex.Dialect);
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE id = 5", false)]
    [InlineData("DELETE FROM orders WHERE region = 'EU'", true)]
    [InlineData("UPDATE orders SET status = 'x' WHERE id = 5", false)]
    [InlineData("UPDATE orders SET status = 'x' WHERE region = 'EU'", true)]
    [InlineData("UPDATE orders SET status = 'x' WHERE region = status", false)]
    public void Dml_RequiresTheFilterOnTheTarget(string sql, bool accepted)
    {
        if (accepted)
        {
            Assert.False(string.IsNullOrEmpty(Compile(sql).Sql));
        }
        else
        {
            Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile(sql)));
        }
    }

    [Theory]
    [InlineData("SELECT id FROM grp WHERE a = 'x'", true)]
    [InlineData("SELECT id FROM grp WHERE b = 'x' AND c = 'y'", true)]
    [InlineData("SELECT id FROM grp WHERE c = 'y' AND b = 'x'", true)]
    [InlineData("SELECT id FROM grp WHERE b = 'x'", false)]
    [InlineData("SELECT id FROM grp WHERE c = 'y'", false)]
    [InlineData("SELECT id FROM grp WHERE b = 'x' AND c = id", false)]
    [InlineData("SELECT id FROM grp", false)]
    [InlineData("SELECT id FROM grp WHERE id = 1", false)]
    public void Groups_AreSatisfiedWhenOneGroupIsFullyConstrained(string sql, bool accepted)
    {
        if (accepted)
        {
            Assert.False(string.IsNullOrEmpty(Compile(sql).Sql));
        }
        else
        {
            Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile(sql)));
        }
    }

    [Fact]
    public void EmptyGroupsOnly_AreNeverSatisfied()
    {
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Grp, Cols("id", "a"), null, ImmutableArray<string>.Empty, 1,
                ImmutableArray.Create(ImmutableArray<string>.Empty))
        }, "dbo");
        Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile("SELECT id FROM grp WHERE a = 'x'", catalog)));
    }

    [Fact]
    public void GroupsTakePrecedenceOverTheFlatColumns()
    {
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Grp, Cols("id", "a", "b"), null, ImmutableArray.Create("a"), 1,
                ImmutableArray.Create(ImmutableArray.Create("b")))
        }, "dbo");
        Assert.False(string.IsNullOrEmpty(Compile("SELECT id FROM grp WHERE b = 'x'", catalog).Sql));
        Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile("SELECT id FROM grp WHERE a = 'x'", catalog)));
    }

    [Fact]
    public void TwoFlatColumns_BothMustBeConstrained()
    {
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Grp, Cols("id", "a", "b"), null, ImmutableArray.Create("a", "b"), 1)
        }, "dbo");
        Assert.False(string.IsNullOrEmpty(Compile("SELECT id FROM grp WHERE a = 'x' AND b = 'y'", catalog).Sql));
        Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile("SELECT id FROM grp WHERE a = 'x'", catalog)));
        Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile("SELECT id FROM grp WHERE b = 'y'", catalog)));
    }

    [Fact]
    public void TenantColumn_SatisfiesItsOwnRequirementWithoutAWhere()
    {
        var compiled = Compile("SELECT id FROM orders", Catalog(ordersRequireTenant: true));
        Assert.Contains("tenant_id", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters.ToArray(), p => Equals(p.Value, "acme"));
    }

    [Fact]
    public void RowPolicy_SatisfiesTheRequirementOnlyForAReferencedColumn()
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_r"] = new PolicyValue("EU", SqlParameterType.String) });
        Assert.False(string.IsNullOrEmpty(Compile("SELECT id FROM orders").Sql));

        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("status", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_r"] = new PolicyValue("EU", SqlParameterType.String) });
        Assert.IsType<RequiredFilterColumnMissingException>(Record.Exception(() => Compile("SELECT id FROM orders")));
    }

    [Fact]
    public void CteShadowingARequiredTable_IsNotCheckedAsTheTable()
    {
        var ex = Record.Exception(() => Compile("WITH orders AS (SELECT id FROM refs) SELECT id FROM orders"));
        Assert.IsNotType<RequiredFilterColumnMissingException>(ex);
    }

    [Theory]
    [InlineData("region = 'EU'", "region", true)]
    [InlineData("status = 'x' AND region = 'EU' AND amount > 1", "region", true)]
    [InlineData("status = 'x' AND region = 'EU' AND amount > 1", "amount", true)]
    [InlineData("status = 'x' AND region = 'EU' AND amount > 1", "status", true)]
    [InlineData("region = 'EU' OR status = 'x'", "region", false)]
    [InlineData("region = 'EU' OR region = 'US'", "region", true)]
    [InlineData("region = status", "region", false)]
    [InlineData("region IN ('a', 'b')", "region", true)]
    [InlineData("region BETWEEN 1 AND 2", "region", true)]
    [InlineData("region = NULL", "region", false)]
    [InlineData("", "region", false)]
    [InlineData("   ", "region", false)]
    [InlineData("region = 'EU' AND AND", "region", false)]
    public void ExtractSingleTableConstrainedColumns_UsesTheSameRules(string predicate, string column, bool constrained)
    {
        var result = ReqVisitor.ExtractSingleTableConstrainedColumns(predicate, new[] { "region", "status", "amount" });
        Assert.Equal(constrained, result.Contains(column));
        if (!constrained) Assert.DoesNotContain(column, result);
    }

    [Fact]
    public void ExtractSingleTableConstrainedColumns_FailsClosedWithoutColumns()
    {
        Assert.Empty(ReqVisitor.ExtractSingleTableConstrainedColumns("region = 'EU'", Array.Empty<string>()));
        Assert.Empty(ReqVisitor.ExtractSingleTableConstrainedColumns(null, new[] { "region" }));
        Assert.Throws<ArgumentNullException>(() => ReqVisitor.ExtractSingleTableConstrainedColumns("region = 'EU'", null!));
        Assert.Equal(new[] { "region", "status" }, ReqVisitor.ExtractSingleTableConstrainedColumns("status = 'x' AND region = 'EU'", new[] { "region", "status", "amount" }));
        Assert.Empty(ReqVisitor.ExtractSingleTableConstrainedColumns("region = (SELECT 1) AND 1 = 1 UNION", new[] { "region" }));
    }

    [Fact]
    public void IsNullLiteral_SeesThroughParenthesesAndCasts()
    {
        var nullLiteral = new LiteralExpression(null, LiteralType.Null);
        Assert.True(ReqVisitor.IsNullLiteral(nullLiteral));
        Assert.True(ReqVisitor.IsNullLiteral(new ParenthesizedExpression(nullLiteral)));
        Assert.True(ReqVisitor.IsNullLiteral(new CastExpression(new ParenthesizedExpression(nullLiteral), "varchar")));
        Assert.False(ReqVisitor.IsNullLiteral(new LiteralExpression("x", LiteralType.String)));
        Assert.False(ReqVisitor.IsNullLiteral(new CastExpression(new LiteralExpression("x", LiteralType.String), "varchar")));
    }
}
