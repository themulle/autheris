using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// SEC-ADG-11 (mutation gate M-12): tables inside a policy subquery get the tenant predicate and the catalog's canonical name,
/// policy subqueries are one level deep, an unknown table is rejected and the table is recorded as a cache dependency.
/// </summary>
[Trait("Category", "SqlGate")]
public class PolicySubqueryTenantRewriterTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();

    private static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p)).ToList()));

    private static SelectStatement Select(string schema, string table, string? alias, Expression? where) => new(null,
        new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(Col("one"), null) },
            new NamedTableSource(new SqlQualifiedName(schema, table), alias is null ? null : new SqlIdentifier(alias)),
            where, null, null), null, null);

    private static PolicyPredicate Policy(Expression expression) => PolicyPredicate.Create(expression, new Dictionary<string, PolicyValue>());

    private CompiledSql Compile(ITableCatalog? catalog = null, FastSqlEngine? engine = null) =>
        (engine ?? _engine).Compile("SELECT id FROM orders".AsMemory(), new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = catalog ?? Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None);

    private static int Count(string text, string fragment) => (text.Length - text.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    [Fact]
    public void SiblingPolicySubqueries_AreBothAccepted_AndBothGetTheTenantPredicate()
    {
        // the depth counter must return to zero after the first subquery, otherwise the second one looks nested
        _rowFilters.Predicates[Orders] = Policy(new BinaryExpression(
            new ExistsExpression(Select("dbo", "Entitlements", "e1", null)), BinaryOperator.Or,
            new ExistsExpression(Select("dbo", "Entitlements", "e2", null))));

        var sql = Compile().Sql;

        Assert.Equal(2, Count(sql, "FROM \"dbo\".\"Entitlements\" WHERE (\"TenantId\" = $1"));
    }

    [Fact]
    public void NestedPolicySubquery_IsRejected()
    {
        var inner = new ExistsExpression(Select("dbo", "Lookup", null, null));
        _rowFilters.Predicates[Orders] = Policy(new ExistsExpression(Select("dbo", "Entitlements", "e", inner)));

        var ex = Assert.ThrowsAny<Exception>(() => Compile());

        Assert.IsAssignableFrom<SecurityException>(ex);
        Assert.Equal("Nested policy subqueries are not permitted.", ex.Message);
    }

    [Fact]
    public void PolicySubqueryOverAnUnknownTable_IsRejected()
    {
        _rowFilters.Predicates[Orders] = Policy(new ExistsExpression(Select("dbo", "Ghost", "g", null)));

        var ex = Assert.ThrowsAny<Exception>(() => Compile());

        Assert.IsAssignableFrom<SecurityException>(ex);
        Assert.Equal("A policy subquery references a table that is not in the catalog.", ex.Message);
    }

    [Fact]
    public void PolicySubqueryOverATableWithoutTenantColumn_IsOnlyCanonicalised()
    {
        _rowFilters.Predicates[Orders] = Policy(new ExistsExpression(Select("DBO", "lookup", "l", null)));

        var sql = Compile().Sql;

        // the cutover projects the cataloged columns of every table; a table without a tenant column gets no tenant filter
        Assert.Contains("(SELECT \"Code\" AS \"Code\" FROM \"dbo\".\"Lookup\") AS \"l\"", sql);
    }

    [Fact]
    public void PolicySubqueryOverATenantTable_IsWrappedInATenantFilteredDerivedTable_WithTheUserAlias()
    {
        _rowFilters.Predicates[Orders] = Policy(new ExistsExpression(Select("dbo", "entitlements", "ent", null)));

        var sql = Compile().Sql;

        // canonical name, every catalog column projected, tenant predicate inside, the user's alias outside
        Assert.Contains("FROM \"dbo\".\"Entitlements\"", sql);
        Assert.Contains("\"OrderId\"", sql);
        Assert.Contains("\"TenantId\"", sql);
        Assert.Contains("AS \"ent\"", sql);
        Assert.Contains("FROM \"dbo\".\"Entitlements\" WHERE (\"TenantId\" = $1", sql);
    }

    [Fact]
    public void PolicySubqueryWithoutAlias_UsesTheUserTableNameAsAlias()
    {
        _rowFilters.Predicates[Orders] = Policy(new ExistsExpression(Select("dbo", "Entitlements", null, null)));

        var sql = Compile().Sql;

        Assert.Contains("AS \"entitlements\"", sql);
    }

    [Fact]
    public void PolicySubqueryTable_IsACacheDependency_SoACatalogChangeInvalidatesTheCachedStatement()
    {
        _rowFilters.Predicates[Orders] = Policy(new ExistsExpression(Select("dbo", "Entitlements", "e", null)));
        var engine = new FastSqlEngine();

        var first = Compile(Catalog(), engine);
        Assert.DoesNotContain("\"NewColumn\"", first.Sql);

        var changed = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, Catalog().Resolve(new SqlQualifiedName("dbo", "Orders"))!.Columns, "TenantId", 1),
            new TableCatalogEntry(Entitlements, ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int"), new CatalogColumn("NewColumn", "int")),
                "TenantId", 2),
            new TableCatalogEntry(Lookup, ImmutableArray.Create(new CatalogColumn("Code", "nvarchar(10)")), null, 1)
        }, "dbo");

        var second = Compile(changed, engine);

        Assert.Contains("\"NewColumn\"", second.Sql);
    }
}
