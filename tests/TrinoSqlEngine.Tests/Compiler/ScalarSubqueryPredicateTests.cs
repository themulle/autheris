using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X0-fix item 4: a scalar subquery in a select list kept the projection context of the outer query, so its injected WHERE
/// predicate was emitted as the integer <c>CASE WHEN ... THEN 1 ELSE 0 END</c> that SQL Server and Oracle reject as a condition.
/// </summary>
public class ScalarSubqueryPredicateTests
{
    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(new TableIdentity("dbo", "Orders"), ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)")), "TenantId", 1)
    }, "dbo");

    private static string Sql(string sql, TargetSqlDialect dialect) =>
        new FastSqlEngine().Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = Catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None).Sql;

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void ScalarSubqueryInTheSelectList_KeepsItsWhereAsACondition(TargetSqlDialect dialect)
    {
        string sql = Sql("SELECT (SELECT count(*) FROM orders) AS n FROM orders WHERE id = 1", dialect);
        Assert.DoesNotContain("WHERE CASE WHEN", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BooleanProjectionAfterTheSubquery_IsStillAValue()
    {
        string sql = Sql("SELECT (SELECT count(*) FROM orders) AS n, id = 1 AS flag FROM orders", TargetSqlDialect.SqlServer);
        Assert.Contains("CASE WHEN ", sql[sql.IndexOf("AS [n]", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }
}
