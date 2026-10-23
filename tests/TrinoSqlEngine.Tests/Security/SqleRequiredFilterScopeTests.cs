using System.Collections.Immutable;
using Autheris.Application.Sql.Errors;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>SQLE-08: the required-filter guard resolves the qualifier against the FROM scope; every ambiguity is "not satisfied".</summary>
[Trait("Category", "Security")]
public sealed class SqleRequiredFilterScopeTests
{
    private static readonly TableIdentity Orders = new("dbo", "orders");
    private static readonly TableIdentity Regions = new("dbo", "regions");

    private static CompileRequest Request()
    {
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders,
                ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("tenant_id", "varchar"), new CatalogColumn("region", "varchar")),
                "tenant_id", ImmutableArray.Create("region"), 1),
            new TableCatalogEntry(Regions,
                ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("name", "varchar")),
                null, ImmutableArray<string>.Empty, 1)
        }, "dbo");

        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    private static CompiledSql Compile(string sql) => new FastSqlEngine().Compile(sql.AsMemory(), Request(), default);

    private static void AssertRejected(string sql) =>
        Assert.Throws<RequiredFilterColumnMissingException>(() => Compile(sql));

    [Fact]
    public void AliasedTable_DerivedTableNamedLikeTable_Rejected() =>
        AssertRejected("SELECT o.id FROM orders o JOIN (SELECT 1 AS region) orders ON 1 = 1 WHERE orders.region = 'EU'");

    [Fact]
    public void AliasedTable_QualifiedByTableName_Rejected() =>
        AssertRejected("SELECT id FROM orders o WHERE orders.region = 'EU'");

    [Fact]
    public void AliasedTable_QualifiedByAlias_Accepted() =>
        Assert.NotNull(Compile("SELECT o.id FROM orders o WHERE o.region = 'EU'"));

    [Fact]
    public void UnaliasedTable_QualifiedByName_Accepted() =>
        Assert.NotNull(Compile("SELECT orders.id FROM orders WHERE orders.region = 'EU'"));

    [Fact]
    public void Unqualified_WithDerivedExposingColumn_Rejected() =>
        AssertRejected("SELECT o.id FROM orders o JOIN (SELECT name AS region FROM regions) d ON 1 = 1 WHERE region = 'EU'");

    [Fact]
    public void Unqualified_SingleSource_Accepted() =>
        Assert.NotNull(Compile("SELECT id FROM orders WHERE region = 'EU'"));

    [Fact]
    public void ThreePart_AliasedTable_Rejected() =>
        AssertRejected("SELECT o.id FROM orders o WHERE dbo.orders.region = 'EU'");
}
