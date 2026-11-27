using System.Collections.Immutable;
using Autheris.Application.Analytics;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

public sealed class MetricViewStatementGuardTests
{
    private static TableMetadata MakeMetricView(
        int? minGroupSize = null,
        string? countMeasure = null,
        string? tenantColumn = null) =>
        new()
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            TenantColumnName = tenantColumn,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                ObjectKind = DiscoveryObjectKind.MetricView,
                MinGroupSize = minGroupSize,
                MetricViewCountMeasure = countMeasure,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension },
                new TableColumn { ColumnName = "order_date", DataType = "timestamp", Role = MetricViewColumnRole.Dimension },
                new TableColumn { ColumnName = "tenant_id", DataType = "string", Role = MetricViewColumnRole.Dimension },
                new TableColumn { ColumnName = "revenue", DataType = "decimal(18,2)", Role = MetricViewColumnRole.Measure },
                new TableColumn { ColumnName = "row_count", DataType = "bigint", Role = MetricViewColumnRole.Measure },
                new TableColumn { ColumnName = "undeclared_col", DataType = "string", DiscoveryHidden = true, Role = null }
            ]
        };

    private static TableAccessDecision AllowAll(TableMetadata mv, string? rowFilterSql = null) =>
        TableAccessDecision.Allowed(
            mv.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["order_date"] = ColumnAccessLevel.Clear,
                ["tenant_id"] = ColumnAccessLevel.Clear,
                ["revenue"] = ColumnAccessLevel.Clear,
                ["row_count"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: rowFilterSql);

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SELECT * FROM analytics.sales_mv")]
    public void SelectStar_IsRefused(string sql)
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable(sql, mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void BareMeasure_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, revenue FROM analytics.sales_mv GROUP BY region, revenue", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MeasureOfDimension_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(region) FROM analytics.sales_mv GROUP BY region", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MeasureOfUnknownColumn_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(does_not_exist) FROM analytics.sales_mv GROUP BY region", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Join_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT m.region, MEASURE(m.revenue) FROM analytics.sales_mv m JOIN analytics.other o ON m.region = o.region GROUP BY m.region", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Subquery_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region FROM (SELECT region FROM analytics.sales_mv) s", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.Oracle)]
    public void NonDatabricksDialect_IsRefused(DatabaseDialect dialect)
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", mv, AllowAll(mv), dialect, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WhereOnMeasure_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(revenue) FROM analytics.sales_mv WHERE MEASURE(revenue) > 10 GROUP BY region", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NestedMeasure_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(MEASURE(revenue)) FROM analytics.sales_mv GROUP BY region", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    public void GroupByAll_IsAllowed()
    {
        var mv = MakeMetricView();
        Should.NotThrow(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY ALL", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true));
    }

    [Fact]
    public void HavingWithMeasure_IsAllowed()
    {
        var mv = MakeMetricView();
        Should.NotThrow(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region HAVING MEASURE(revenue) > 100", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RowPolicyOnNonDimension_IsRefused()
    {
        var mv = MakeMetricView();
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable(
                "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region",
                mv,
                AllowAll(mv, rowFilterSql: "revenue > 0"),
                DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TenantPredicateOnDimension_IsAppliedBeforeAggregation()
    {
        var mv = MakeMetricView(tenantColumn: "tenant_id");
        Should.NotThrow(() =>
            MetricViewStatementGuard.EnforceForSingleTable(
                "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region",
                mv,
                AllowAll(mv, rowFilterSql: "region = 'EU'"),
                DatabaseDialect.Databricks, 0, true));

        var mvBadTenant = MakeMetricView(tenantColumn: "revenue");
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable(
                "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region",
                mvBadTenant,
                AllowAll(mvBadTenant),
                DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Measure_WithDivisionCastTimeBucket_UnderTenantPredicate_PassesExpressionGuard()
    {
        var engine = new FastSqlEngine();
        var mvIdentity = new TableIdentity("analytics", "sales_mv");
        var filters = new InMemoryPolicyProvider();
        filters.SetNoPolicy(mvIdentity);
        var catalog = new InMemoryTableCatalog([
            new TableCatalogEntry(mvIdentity, [
                new CatalogColumn("region", "string"),
                new CatalogColumn("order_date", "timestamp"),
                new CatalogColumn("tenant_id", "string"),
                new CatalogColumn("revenue", "decimal(18,2)"),
                new CatalogColumn("row_count", "bigint")
            ], "tenant_id", ImmutableArray<string>.Empty, 1, default, IsMetricView: true)
        ], "analytics");

        var compiled = engine.Compile(
            "SELECT DATE_TRUNC('day', order_date) AS d, CAST(MEASURE(revenue) / 2 AS VARCHAR) AS half_rev FROM analytics.sales_mv GROUP BY DATE_TRUNC('day', order_date)".AsMemory(),
            new CompileRequest
            {
                TargetDialect = TargetSqlDialect.Databricks,
                TokenGuards = SqlTokenSecurityOptions.Strict,
                AllowExperimentalDialect = true,
                TranslateTrinoDateFunctions = true,
                Policy = new GovernancePolicy
                {
                    RowFilters = filters,
                    Masks = EmptyMaskProvider.Instance,
                    Catalog = catalog,
                    Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
                }
            }, CancellationToken.None);

        compiled.Sql.ShouldContain("MEASURE(`revenue`)");
        compiled.Sql.ShouldNotContain("FROM (");
        compiled.Sql.ShouldContain("FROM `analytics`.`sales_mv`");
        compiled.Sql.ShouldContain("WHERE");
        compiled.Sql.ShouldContain("`tenant_id`");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MinGroupSize_WithoutCountMeasure_IsRefused()
    {
        var mv = MakeMetricView(minGroupSize: 5, countMeasure: null);
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", mv, AllowAll(mv), DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MinGroupSize_DropsSmallGroups()
    {
        var mv = MakeMetricView(minGroupSize: 5, countMeasure: "row_count");
        var result = MetricViewStatementGuard.EnforceForSingleTable(
            "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region",
            mv,
            AllowAll(mv),
            DatabaseDialect.Databricks, 0, true);

        result.HiddenCountColumnAlias.ShouldNotBeNullOrWhiteSpace();
        result.EffectiveMinGroupSize.ShouldBe(5);
        result.InternalCountMeasureColumn.ShouldBe("row_count");
        result.AugmentedSql!.ShouldContain($"MEASURE(\"row_count\") AS {result.HiddenCountColumnAlias}");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["region"] = "EU", ["revenue"] = 100m, [result.HiddenCountColumnAlias!] = 10L },
            new Dictionary<string, object?> { ["region"] = "US", ["revenue"] = 20m, [result.HiddenCountColumnAlias!] = 3L }
        ];

        var filtered = GroupSizeFilterHelper.FilterMetricViewRows(rows, result.EffectiveMinGroupSize, result.HiddenCountColumnAlias!);
        filtered.Count.ShouldBe(1);
        filtered[0]["region"].ShouldBe("EU");
        filtered[0].ContainsKey(result.HiddenCountColumnAlias!).ShouldBeFalse();
    }

    public static IEnumerable<object[]> InjectEdgeCaseSql() =>
    [
        ["SELECT 'FROM' AS f, region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region"],
        ["SELECT region, MEASURE(revenue) FROM analytics.sales_mv -- comment FROM\nGROUP BY region"],
        ["SELECT region, MEASURE(revenue) /* comment FROM */ FROM analytics.sales_mv GROUP BY region"],
        ["SELECT region, MEASURE(revenue)FROM analytics.sales_mv GROUP BY region"]
    ];

    [Theory]
    [MemberData(nameof(InjectEdgeCaseSql))]
    public void InjectHiddenCountMeasure_ParsesAfterCommentAndLiteralEdgeCases(string sql)
    {
        var mv = MakeMetricView(minGroupSize: 5, countMeasure: "row_count");
        var result = MetricViewStatementGuard.EnforceForSingleTable(
            sql,
            mv,
            AllowAll(mv),
            DatabaseDialect.Databricks, 0, true);

        result.AugmentedSql.ShouldNotBeNull();
        var engine = new FastSqlEngine();
        var (tree, _) = engine.Parse(result.AugmentedSql!.AsMemory());
        tree.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnparsableSqlContainingMeasureKeyword_WithoutMetricViewMetadata_IsRefusedFailClosed()
    {
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.Enforce(
                "SELECT *** MEASURE(x) FROM regular_table",
                new Dictionary<TableIdentity, TableMetadata>(),
                new Dictionary<TableIdentity, TableAccessDecision>(),
                DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", "region", ColumnAccessLevel.Deny)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", "region", ColumnAccessLevel.Mask)]
    [InlineData("SELECT MEASURE(revenue) FROM analytics.sales_mv WHERE order_date > TIMESTAMP '2024-01-01 00:00:00'", "order_date", ColumnAccessLevel.Deny)]
    [InlineData("SELECT order_date, MEASURE(revenue) FROM analytics.sales_mv GROUP BY order_date", "order_date", ColumnAccessLevel.Mask)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", "revenue", ColumnAccessLevel.Deny)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", "revenue", ColumnAccessLevel.Mask)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY ALL", "region", ColumnAccessLevel.Deny)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region HAVING MEASURE(row_count) > 1", "row_count", ColumnAccessLevel.Deny)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region ORDER BY MEASURE(row_count)", "row_count", ColumnAccessLevel.Mask)]
    [InlineData("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region ORDER BY order_date", "order_date", ColumnAccessLevel.Deny)]
    public void UserQueryReferencingNonClearDimensionOrMeasure_IsRefused(string sql, string targetColumn, ColumnAccessLevel restrictedLevel)
    {
        var mv = MakeMetricView();
        var levels = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = ColumnAccessLevel.Clear,
            ["order_date"] = ColumnAccessLevel.Clear,
            ["tenant_id"] = ColumnAccessLevel.Clear,
            ["revenue"] = ColumnAccessLevel.Clear,
            ["row_count"] = ColumnAccessLevel.Clear,
            [targetColumn] = restrictedLevel
        };
        var decision = TableAccessDecision.Allowed(mv.Identifier, levels);

        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable(sql, mv, decision, DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MinGroupSize_InternalDeniedCountMeasure_AllowsInjectionWhenNotUserSelectedAndRefusesMasked()
    {
        var mv = MakeMetricView(minGroupSize: 5, countMeasure: "row_count");
        var denyCountLevels = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = ColumnAccessLevel.Clear,
            ["revenue"] = ColumnAccessLevel.Clear,
            ["row_count"] = ColumnAccessLevel.Deny
        };

        var guard = MetricViewStatementGuard.EnforceForSingleTable(
            "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region",
            mv,
            TableAccessDecision.Allowed(mv.Identifier, denyCountLevels),
            DatabaseDialect.Databricks, 0, true);

        guard.InternalCountMeasureColumn.ShouldBe("row_count");
        guard.AugmentedSql!.ShouldContain("MEASURE(\"row_count\")");

        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable(
                "SELECT region, MEASURE(revenue), MEASURE(row_count) FROM analytics.sales_mv GROUP BY region",
                mv,
                TableAccessDecision.Allowed(mv.Identifier, denyCountLevels),
                DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);

        var maskedCountLevels = new Dictionary<string, ColumnAccessLevel>(denyCountLevels, StringComparer.OrdinalIgnoreCase)
        {
            ["row_count"] = ColumnAccessLevel.Mask
        };
        Should.Throw<WebSqlPolicyException>(() =>
            MetricViewStatementGuard.EnforceForSingleTable(
                "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region",
                mv,
                TableAccessDecision.Allowed(mv.Identifier, maskedCountLevels),
                DatabaseDialect.Databricks, 0, true))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    private const string ByOrderDate = "SELECT order_date, MEASURE(revenue) FROM analytics.sales_mv WHERE {0} GROUP BY order_date";

    private static string Fill(string template, string predicate) =>
        template.Replace("{0}", predicate, StringComparison.Ordinal);

    private static MetricViewGuardResult Run(string sql, TableMetadata mv, int floor, bool restrictNegatedFilters = true) =>
        MetricViewStatementGuard.EnforceForSingleTable(sql, mv, AllowAll(mv), DatabaseDialect.Databricks, floor, restrictNegatedFilters);

    private static void ShouldRefuse(string sql, TableMetadata mv, int floor = 5, bool restrictNegatedFilters = true) =>
        Should.Throw<WebSqlPolicyException>(() => Run(sql, mv, floor, restrictNegatedFilters))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);

    [Fact]
    [Trait("Category", "Security")]
    public void PassedFloor_WithoutCatalogValue_InjectsHiddenCountMeasure()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        var result = Run("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", mv, 5);

        result.EffectiveMinGroupSize.ShouldBe(5);
        result.InternalCountMeasureColumn.ShouldBe("row_count");
        result.AugmentedSql!.ShouldContain("MEASURE(\"row_count\")");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PassedFloor_WithoutCountMeasure_IsRefused() =>
        ShouldRefuse("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", MakeMetricView());

    [Fact]
    [Trait("Category", "Security")]
    public void PassedFloorOfOne_DoesNotInjectCountMeasure()
    {
        var mv = MakeMetricView();
        var result = Run("SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region", mv, 1);

        result.AugmentedSql.ShouldBeNull();
        result.EffectiveMinGroupSize.ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("region <> 'EU'")]
    [InlineData("region != 'EU'")]
    [InlineData("region NOT IN ('EU', 'US')")]
    [InlineData("region NOT LIKE 'E%'")]
    [InlineData("region NOT BETWEEN 'A' AND 'F'")]
    [InlineData("NOT (region = 'EU')")]
    public void NegatedWhere_OnNonGroupedDimension_IsRefused(string predicate)
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        ShouldRefuse(Fill(ByOrderDate, predicate), mv);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NegatedWhere_WithNegationGuardOff_IsAccepted()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        Run(Fill(ByOrderDate, "region <> 'EU'"), mv, 5, restrictNegatedFilters: false).AugmentedSql.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NegatedWhere_WithKOfOne_IsAccepted()
    {
        var mv = MakeMetricView();
        Should.NotThrow(() => Run(Fill(ByOrderDate, "region <> 'EU'"), mv, 1));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("region <> 'EU'")]
    [InlineData("region NOT IN ('EU')")]
    [InlineData("NOT (region = 'EU')")]
    public void NegatedWhere_OnBareGroupedColumn_IsAccepted(string predicate)
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        Run($"SELECT region, MEASURE(revenue) FROM analytics.sales_mv WHERE {predicate} GROUP BY region", mv, 5).AugmentedSql.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NegatedWhere_OnColumnGroupedOnlyInsideExpression_IsRefused()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        const string Sql = "SELECT CAST(region AS STRING) AS r, MEASURE(revenue) FROM analytics.sales_mv WHERE {0} GROUP BY CAST(region AS STRING)";
        Should.NotThrow(() => Run(Fill(Sql, "region = 'EU'"), mv, 5));
        ShouldRefuse(Fill(Sql, "region <> 'EU'"), mv);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GroupByAll_BareProjectionColumn_IsExemptFromNegationCheck()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        Run("SELECT region, MEASURE(revenue) FROM analytics.sales_mv WHERE region <> 'EU' GROUP BY ALL", mv, 5)
            .AugmentedSql.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GroupByAll_ExpressionProjection_IsNotExempt()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        const string Sql = "SELECT CAST(region AS STRING) AS r, MEASURE(revenue) FROM analytics.sales_mv WHERE {0} GROUP BY ALL";
        Should.NotThrow(() => Run(Fill(Sql, "region = 'EU'"), mv, 5));
        ShouldRefuse(Fill(Sql, "region <> 'EU'"), mv);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("\"REGION\" <> 'EU'")]
    [InlineData("sales_mv.region <> 'EU'")]
    [InlineData("analytics.sales_mv.Region != 'EU'")]
    [InlineData("`Region` <> 'EU'")]
    public void NegatedWhere_QuotedQualifiedAndMixedCaseColumn_IsRefused(string predicate)
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        ShouldRefuse(Fill(ByOrderDate, predicate), mv);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParameterIdentifier_DoesNotHideColumnReference()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        ShouldRefuse(Fill(ByOrderDate, "region <> __param_p0"), mv);
        ShouldRefuse(Fill(ByOrderDate, "__param_p0 <> region"), mv);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ColumnNamedWithParamPrefix_IsStillTreatedAsColumn()
    {
        var baseView = MakeMetricView(countMeasure: "row_count");
        var mv = baseView with
        {
            Columns = [.. baseView.Columns, new TableColumn { ColumnName = "__param_region", DataType = "string", Role = MetricViewColumnRole.Dimension }]
        };
        var decision = TableAccessDecision.Allowed(
            mv.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["order_date"] = ColumnAccessLevel.Clear,
                ["revenue"] = ColumnAccessLevel.Clear,
                ["row_count"] = ColumnAccessLevel.Clear,
                ["__param_region"] = ColumnAccessLevel.Clear
            });

        Should.Throw<WebSqlPolicyException>(() => MetricViewStatementGuard.EnforceForSingleTable(
            Fill(ByOrderDate, "__param_region <> 'EU'"), mv, decision, DatabaseDialect.Databricks, 5, true));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnknownNonParamIdentifier_IsRefused()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        ShouldRefuse(Fill(ByOrderDate, "mystery_col = 'x'"), mv);
        ShouldRefuse(Fill(ByOrderDate, "mystery_col <> 'x'"), mv);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Having_OnNonGroupedDimension_IsRefused()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        const string Sql = "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region HAVING order_date > 5";
        ShouldRefuse(Sql, mv);
        Should.NotThrow(() => Run(Sql, mv, 5, restrictNegatedFilters: false));
        Should.NotThrow(() => Run(
            "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region HAVING region = 'EU'", mv, 5));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RefusalMessage_ContainsNoSqlOrColumnNames()
    {
        var mv = MakeMetricView(countMeasure: "row_count");
        var ex = Should.Throw<WebSqlPolicyException>(() => Run(Fill(ByOrderDate, "region <> 'EU'"), mv, 5));

        ex.Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
        ex.Message.ShouldNotContain("region");
        ex.Message.ShouldNotContain("sales_mv");
        ex.Message.ShouldNotContain("5");
        ex.Message.ShouldNotContain("EU");
    }

    private sealed class InMemoryPolicyProvider : IPolicyPredicateProvider
    {
        private readonly HashSet<TableIdentity> _noPolicy = [];
        public void SetNoPolicy(TableIdentity id) => _noPolicy.Add(id);
        public bool ShouldApplyPolicy(TableIdentity table) => !_noPolicy.Contains(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class EmptyMaskProvider : IColumnMaskProvider
    {
        public static readonly EmptyMaskProvider Instance = new();
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }
}
