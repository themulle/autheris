namespace TrinoSqlEngine.Tests.Compiler;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class MetricViewPushdownSecurityTests
{
    private static readonly TableIdentity SalesMv = new("analytics", "sales_mv");

    private static FastSqlEngine CreateEngine() => new();

    private static PolicyPredicate BuildTargetRegionPredicate() =>
        PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier(RowFilterAliases.Target, false), new SqlIdentifier("region", true)])),
                BinaryOperator.Equal,
                new PolicyParameterExpression("p_rls_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["p_rls_region"] = new("EU", SqlParameterType.String) });

    private static (GovernancePolicy Policy, InMemoryPolicyPredicateProvider Filters, InMemoryMaskProvider Masks) BuildPolicy(
        string? tenantCol = "tenant_id",
        PolicyPredicate? rowFilter = null,
        IReadOnlyDictionary<string, MaskSpec>? masks = null,
        PerCellTablePolicy? perCell = null,
        bool registerOtherTable = false)
    {
        var mvEntry = new TableCatalogEntry(
            SalesMv,
            [
                new CatalogColumn("region", "string"),
                new CatalogColumn("order_date", "timestamp"),
                new CatalogColumn("tenant_id", "string"),
                new CatalogColumn("revenue", "decimal(18,2)"),
                new CatalogColumn("row_count", "bigint")
            ],
            tenantCol,
            ImmutableArray<string>.Empty,
            Version: 1,
            default,
            IsMetricView: true);

        var entries = new List<TableCatalogEntry> { mvEntry };
        if (registerOtherTable)
        {
            entries.Add(new TableCatalogEntry(
                new TableIdentity("analytics", "other"),
                [
                    new CatalogColumn("region", "string"),
                    new CatalogColumn("tenant_id", "string")
                ],
                "tenant_id",
                Version: 1));
        }

        var catalog = new InMemoryTableCatalog(entries, "analytics");
        var filters = new InMemoryPolicyPredicateProvider();
        if (rowFilter is null)
        {
            filters.SetNoPolicy(SalesMv);
        }
        else
        {
            filters.SetPredicate(SalesMv, rowFilter);
        }

        var maskProvider = new InMemoryMaskProvider(masks);
        var perCellProvider = perCell is null
            ? EmptyPerCellColumnProvider.Instance
            : (IPerCellColumnProvider)new SingleTablePerCellProvider(SalesMv, perCell);

        var policy = new GovernancePolicy
        {
            Catalog = catalog,
            RowFilters = filters,
            Masks = maskProvider,
            PerCellColumns = perCellProvider,
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        };
        return (policy, filters, maskProvider);
    }

    private static CompileRequest DatabricksRequest(GovernancePolicy policy) => new()
    {
        TargetDialect = TargetSqlDialect.Databricks,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        AllowExperimentalDialect = true,
        AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "measure", "date_trunc" },
        Policy = policy
    };

    [Fact]
    [Trait("Category", "Security")]
    public void DirectFromPushdown_EmitsTenantAndRowPolicyAndParenthesizesOrWhere_WithoutSubqueryWrap()
    {
        var (policy, _, _) = BuildPolicy(rowFilter: BuildTargetRegionPredicate());

        var compiled = CreateEngine().Compile(
            "SELECT m.region, MEASURE(m.revenue) AS total_rev FROM analytics.sales_mv m WHERE m.region = 'EU' OR m.region = 'DE' GROUP BY ALL".AsMemory(),
            DatabricksRequest(policy),
            CancellationToken.None);

        Assert.DoesNotContain("FROM (", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM `analytics`.`sales_mv` AS `m` WHERE", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("`tenant_id`", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("BINARY)", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("(`m`.`region` = :", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("AND (`m`.`region` = :", compiled.Sql, StringComparison.Ordinal);
        Assert.Equal([false, false], compiled.OutputColumnMasks.ToArray());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SELECT * FROM analytics.sales_mv")]
    [InlineData("SELECT s.region FROM (SELECT region FROM analytics.sales_mv) s")]
    [InlineData("WITH c AS (SELECT region FROM analytics.sales_mv) SELECT region FROM c")]
    [InlineData("SELECT m.region FROM analytics.sales_mv m JOIN analytics.other o ON m.region = o.region GROUP BY m.region")]
    [InlineData("SELECT region FROM analytics.sales_mv GROUP BY region HAVING region IN (SELECT region FROM analytics.sales_mv)")]
    public void MetricView_UnsupportedContainerOrWildcard_FailsClosed(string sql)
    {
        var (policy, _, _) = BuildPolicy(registerOtherTable: true);

        Assert.ThrowsAny<SecurityException>(() =>
            CreateEngine().Compile(sql.AsMemory(), DatabricksRequest(policy), CancellationToken.None));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region")]
    [InlineData("SELECT region, MEASURE(row_count) FROM analytics.sales_mv WHERE revenue > 0 GROUP BY region")]
    [InlineData("SELECT region, MEASURE(row_count) FROM analytics.sales_mv GROUP BY region, revenue")]
    [InlineData("SELECT region, MEASURE(row_count) FROM analytics.sales_mv GROUP BY region HAVING MEASURE(revenue) > 1")]
    [InlineData("SELECT region, MEASURE(row_count) FROM analytics.sales_mv GROUP BY region ORDER BY MEASURE(revenue)")]
    public void MetricView_MaskedOrDeniedColumnInAnyClause_FailsClosed(string sql)
    {
        var masks = new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["revenue"] = new(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty)
        };
        var (policy, _, _) = BuildPolicy(masks: masks);

        Assert.ThrowsAny<SecurityException>(() =>
            CreateEngine().Compile(sql.AsMemory(), DatabricksRequest(policy), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MetricView_PerCellPolicy_FailsClosed()
    {
        var colRule = new PerCellColumnRule(
            ImmutableArray.Create(0),
            ImmutableArray.Create(1));
        var perCell = new PerCellTablePolicy(
            [new PerCellSignatureSpec(BuildTargetRegionPredicate()), new PerCellSignatureSpec(BuildTargetRegionPredicate())],
            new Dictionary<string, PerCellColumnRule>(StringComparer.OrdinalIgnoreCase) { ["revenue"] = colRule });
        var (policy, _, _) = BuildPolicy(perCell: perCell);

        Assert.Throws<SecurityException>(() =>
            CreateEngine().Compile(
                "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region".AsMemory(),
                DatabricksRequest(policy),
                CancellationToken.None));
    }

    private sealed class InMemoryPolicyPredicateProvider : IPolicyPredicateProvider
    {
        private readonly HashSet<TableIdentity> _noPolicy = [];
        private readonly Dictionary<TableIdentity, PolicyPredicate> _predicates = [];
        public void SetNoPolicy(TableIdentity id) => _noPolicy.Add(id);
        public void SetPredicate(TableIdentity id, PolicyPredicate predicate) => _predicates[id] = predicate;
        public bool ShouldApplyPolicy(TableIdentity table) => !_noPolicy.Contains(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => _predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
    }

    private sealed class InMemoryMaskProvider(IReadOnlyDictionary<string, MaskSpec>? specs) : IColumnMaskProvider
    {
        private readonly IReadOnlyDictionary<string, MaskSpec> _specs = specs ?? new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
        public bool HasMask(TableIdentity table, string column) => table == SalesMv && _specs.ContainsKey(column);
        public MaskSpec GetMask(TableIdentity table, string column) => _specs[column];
    }

    private sealed class SingleTablePerCellProvider(TableIdentity target, PerCellTablePolicy policy) : IPerCellColumnProvider
    {
        public PerCellTablePolicy? GetPolicy(TableIdentity table) => table == target ? policy : null;
    }
}
