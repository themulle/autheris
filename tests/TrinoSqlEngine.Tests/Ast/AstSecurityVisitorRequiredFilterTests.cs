namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Autheris.Application.Sql.Errors;
using Xunit;

public sealed class AstSecurityVisitorRequiredFilterTests
{
    private static readonly TableIdentity Orders = new("dbo", "orders");
    private static readonly TableIdentity Regions = new("dbo", "regions");

    private static InMemoryTableCatalog CreateCatalog(params string[] requiredCols)
    {
        var ordersCols = ImmutableArray.Create(
            new CatalogColumn("id", "integer"),
            new CatalogColumn("tenant_id", "varchar"),
            new CatalogColumn("region", "varchar"),
            new CatalogColumn("status", "varchar"),
            new CatalogColumn("amount", "decimal(18,2)"),
            new CatalogColumn("created_at", "timestamp"));

        var regionsCols = ImmutableArray.Create(
            new CatalogColumn("id", "integer"),
            new CatalogColumn("name", "varchar"));

        return new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, ordersCols, "tenant_id", requiredCols.ToImmutableArray(), 1),
            new TableCatalogEntry(Regions, regionsCols, null, ImmutableArray<string>.Empty, 1)
        }, "dbo");
    }

    private static CompileRequest CreateRequest(
        ITableCatalog catalog,
        IPolicyPredicateProvider? rowFilters = null,
        TenantBinding? tenant = null)
    {
        var filters = rowFilters ?? new DictPolicyProvider();
        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
            Policy = new GovernancePolicy
            {
                RowFilters = filters,
                Masks = new DictMaskProvider(),
                Catalog = catalog,
                Tenant = tenant ?? new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_03_QueryWithoutWhere_RequiredColumn_RejectedWith400()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT id FROM orders".AsMemory(), request, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_04_QueryWithComparisonFilter_RequiredColumn_Permitted()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        var compiled = engine.Compile("SELECT id FROM orders WHERE region = 'EU'".AsMemory(), request, default);
        Assert.NotNull(compiled);

        var compiledGt = engine.Compile("SELECT id FROM orders WHERE region > 'A'".AsMemory(), request, default);
        Assert.NotNull(compiledGt);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_05_QueryWithInListFilter_RequiredColumn_Permitted()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        var compiled = engine.Compile("SELECT id FROM orders WHERE region IN ('EU', 'US')".AsMemory(), request, default);
        Assert.NotNull(compiled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_06_QueryWithBetweenFilter_RequiredColumn_Permitted()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        var compiled = engine.Compile("SELECT id FROM orders WHERE region BETWEEN 'A' AND 'Z'".AsMemory(), request, default);
        Assert.NotNull(compiled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_07_QueryWithTautologyFilter_colEqualsCol_Rejected()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // Tautology: region = region
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT id FROM orders WHERE region = region".AsMemory(), request, default));

        // Equijoin without bounded literal/parameter filter
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT o.id FROM orders o JOIN regions r ON o.region = r.name".AsMemory(), request, default));

        // Negative operator: region != 'EU'
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT id FROM orders WHERE region != 'EU'".AsMemory(), request, default));

        // IS NOT NULL: region IS NOT NULL
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT id FROM orders WHERE region IS NOT NULL".AsMemory(), request, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_08_QueryWithOrFilter_MissingInOneBranch_Rejected()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // One branch has region, other branch has unrelated amount > 100
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT id FROM orders WHERE region = 'EU' OR amount > 100".AsMemory(), request, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_09_QueryWithOrFilter_PresentInAllBranches_Permitted()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        var compiled = engine.Compile("SELECT id FROM orders WHERE region = 'EU' OR region = 'US'".AsMemory(), request, default);
        Assert.NotNull(compiled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_10_QueryWithoutWhere_ActiveRlsConstrainsColumn_Permitted()
    {
        var catalog = CreateCatalog("region");
        var filters = new DictPolicyProvider();
        filters.NoPolicy.Remove(Orders);
        filters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("region", true) })),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_reg", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_reg"] = new("EU", SqlParameterType.String) });

        var request = CreateRequest(catalog, filters);
        var engine = new FastSqlEngine();

        // No query WHERE clause, but active RLS constrains "region" -> permitted
        var compiled = engine.Compile("SELECT id FROM orders".AsMemory(), request, default);
        Assert.NotNull(compiled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_11_QueryWithoutWhere_ActiveTenantConstrainsColumn_Permitted()
    {
        // Table has tenant_id as required filter column
        var catalog = CreateCatalog("tenant_id");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // No query WHERE clause, but active tenant predicate constrains tenant_id -> permitted
        var compiled = engine.Compile("SELECT id FROM orders".AsMemory(), request, default);
        Assert.NotNull(compiled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_12_QueryInCte_RequiredColumnMissingInCte_Rejected()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // CTE body lacks filter on physical orders table, even though outer query has WHERE region = 'EU'
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("WITH cte AS (SELECT id, region FROM orders) SELECT id FROM cte WHERE region = 'EU'".AsMemory(), request, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_13_QueryInCte_RequiredColumnPresentInCte_Permitted()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // CTE body has filter on physical orders table
        var compiled = engine.Compile("WITH cte AS (SELECT id, region FROM orders WHERE region = 'EU') SELECT id FROM cte".AsMemory(), request, default);
        Assert.NotNull(compiled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_14_QueryInSubquery_RequiredColumnMissingInSubquery_Rejected()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // Subquery lacks filter on physical orders table
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("SELECT id FROM (SELECT id, region FROM orders) sub WHERE region = 'EU'".AsMemory(), request, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_15_TableQueryBody_RequiredColumnMissing_Rejected()
    {
        var catalog = CreateCatalog("region");
        var request = CreateRequest(catalog);
        var engine = new FastSqlEngine();

        // TABLE orders has no WHERE clause
        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            engine.Compile("TABLE orders".AsMemory(), request, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_163_16_SqlCompilerErrorMapper_MapsTo400_WithFixedMessage()
    {
        var ex = new RequiredFilterColumnMissingException();
        bool mapped = SqlCompilerErrorMapper.TryMap(ex, out var mapping);

        Assert.True(mapped);
        Assert.Equal(400, mapping.Status);
        Assert.Equal("REQUIRED_FILTER_COLUMN_MISSING", mapping.Code);
        Assert.Equal("A required filter column was not constrained by the query or an active row policy.", mapping.Title);
    }
}
