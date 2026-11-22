namespace Autheris.Tests.Unit.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Policy;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Security;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using RequiredFilterColumnMissingException = TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException;

[Trait("Category", "Security")]
public sealed class DecisionRowFilterTests
{
    private static readonly FastSqlEngine Engine = new();
    private static readonly PolicyPredicateRenderer Renderer = new();
    private static readonly TableIdentifier OrdersId = new("pg", "public", "orders");
    private static readonly TableIdentifier ItemsId = new("pg", "public", "order_items");
    private static readonly TableIdentifier MetricViewId = new("dbx", "analytics", "sales_mv");

    private static PolicyPredicateRowPolicy TypedRegionPolicy(string region = "EU", TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        var namer = new PolicyParameterNamer("__rf_");
        var param = namer.Add(new PolicyValue(region, SqlParameterType.String));
        var expr = new BinaryExpression(PolicyParameterNamer.Column("region"), BinaryOperator.Equal, param);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expr, namer.Values), dialect, Renderer);
    }

    private static PolicyPredicateRowPolicy TypedMeasurePolicy()
    {
        var namer = new PolicyParameterNamer("__rf_");
        var param = namer.Add(new PolicyValue(0L, SqlParameterType.Int64));
        var expr = new BinaryExpression(PolicyParameterNamer.Column("revenue"), BinaryOperator.GreaterThan, param);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expr, namer.Values), TargetSqlDialect.Databricks, Renderer);
    }

    private static PolicyPredicateRowPolicy TypedSubqueryPolicy()
    {
        var query = new SelectStatement(
            null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                new NamedTableSource(new SqlQualifiedName("allowlist"), null),
                null,
                null,
                null),
            null,
            null);
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(query), new Dictionary<string, PolicyValue>()),
            TargetSqlDialect.PostgreSql,
            Renderer);
    }

    private static TableAccessDecision AllowedOrders(string? rowFilterSql = null, IReadOnlyDictionary<string, object?>? parameters = null) =>
        TableAccessDecision.Allowed(
            OrdersId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear,
                ["status"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: rowFilterSql,
            hasUnconstrainedColumnAllow: true,
            rowFilterParameters: parameters);

    private static TableMetadata OrdersMeta(string[]? requiredColumns = null) => new()
    {
        Identifier = OrdersId,
        Table = new Table
        {
            SourceName = "pg",
            SchemaName = "public",
            TableName = "orders",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            PhysicalDatabaseName = "db_pg",
            RequiredFilterColumns = [.. (requiredColumns ?? [])],
            IsActive = true
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "status", DataType = "varchar" }
        ]
    };

    private static TableMetadata ItemsMeta() => new()
    {
        Identifier = ItemsId,
        Table = new Table
        {
            SourceName = "pg",
            SchemaName = "public",
            TableName = "order_items",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            PhysicalDatabaseName = "db_pg",
            IsActive = true
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "order_id", DataType = "integer" }
        ]
    };

    private static TableMetadata MetricViewMeta() => new()
    {
        Identifier = MetricViewId,
        Table = new Table
        {
            SourceName = "dbx",
            SchemaName = "analytics",
            TableName = "sales_mv",
            SourceType = "Databricks",
            ObjectKind = DiscoveryObjectKind.MetricView,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension },
            new TableColumn { ColumnName = "order_date", DataType = "timestamp", Role = MetricViewColumnRole.Dimension },
            new TableColumn { ColumnName = "revenue", DataType = "decimal(18,2)", Role = MetricViewColumnRole.Measure }
        ]
    };

    [Fact]
    public void HasRowFilter_ReflectsRowPolicyAndCombinedRowFilterSql()
    {
        AllowedOrders().HasRowFilter.ShouldBeFalse();
        AllowedOrders("   ").HasRowFilter.ShouldBeFalse();
        AllowedOrders("region = 'EU'").HasRowFilter.ShouldBeTrue();
        AllowedOrders().WithAdditionalRowPolicy(TypedRegionPolicy()).HasRowFilter.ShouldBeTrue();
        (AllowedOrders() with { RowPolicy = new UnknownRowPolicy(), CombinedRowFilterSql = null }).HasRowFilter.ShouldBeTrue();
    }

    [Fact]
    public void DecisionRowFilter_NoFilter_ReturnsFalse()
    {
        var decision = AllowedOrders();

        DecisionRowFilter.TryResolveTypedPredicate(decision, out var typed).ShouldBeFalse();
        typed.ShouldBeNull();
        DecisionRowFilter.TryResolve(decision, SqlFunctionAllowlists.Ansi, out var resolved).ShouldBeFalse();
        resolved.TypedPredicate.ShouldBeNull();
        DecisionRowFilter.TryResolveExpression(decision, SqlFunctionAllowlists.Ansi, out var expr).ShouldBeFalse();
        expr.ShouldBeNull();
        DecisionRowFilter.IsInlineEligiblePolicyPredicate(decision).ShouldBeFalse();
    }

    [Fact]
    public void DecisionRowFilter_TypedPolicy_PreservesAstAndParametersWithoutReParsing()
    {
        var policy = TypedRegionPolicy("EU' OR '1'='1");
        var decision = AllowedOrders().WithAdditionalRowPolicy(policy);

        DecisionRowFilter.TryResolveTypedPredicate(decision, out var typed).ShouldBeTrue();
        ReferenceEquals(typed, policy.Predicate).ShouldBeTrue();

        DecisionRowFilter.TryResolve(decision, allowedFunctions: null, out var resolved).ShouldBeTrue();
        resolved.IsTyped.ShouldBeTrue();
        ReferenceEquals(resolved.TypedPredicate, policy.Predicate).ShouldBeTrue();
        ReferenceEquals(resolved.Expression, policy.Predicate.Expression).ShouldBeTrue();
        resolved.TypedPredicate!.Parameters.Values.Select(v => v.Value).ShouldContain("EU' OR '1'='1");

        DecisionRowFilter.IsInlineEligiblePolicyPredicate(decision).ShouldBeTrue();
    }

    [Theory]
    [InlineData("region = 'US'")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DecisionRowFilter_DriftedOrBlankCombinedRowFilterSql_FailsClosed(string? driftedSql)
    {
        var decision = AllowedOrders().WithAdditionalRowPolicy(TypedRegionPolicy()) with { CombinedRowFilterSql = driftedSql };

        Should.Throw<GatewaySecurityException>(() => DecisionRowFilter.TryResolveTypedPredicate(decision, out _))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        Should.Throw<GatewaySecurityException>(() => DecisionRowFilter.TryResolve(decision, SqlFunctionAllowlists.Ansi, out _))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        Should.Throw<GatewaySecurityException>(() => DecisionRowFilter.TryResolveExpression(decision, SqlFunctionAllowlists.Ansi, out _))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        DecisionRowFilter.IsInlineEligiblePolicyPredicate(decision).ShouldBeFalse();
    }

    [Theory]
    [InlineData("region = 'EU'")]
    [InlineData(null)]
    public void DecisionRowFilter_UnknownRowPolicy_FailsClosed(string? rowFilterSql)
    {
        var decision = AllowedOrders(rowFilterSql) with { RowPolicy = new UnknownRowPolicy() };

        Should.Throw<GatewaySecurityException>(() => DecisionRowFilter.TryResolveTypedPredicate(decision, out _))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        Should.Throw<GatewaySecurityException>(() => DecisionRowFilter.TryResolve(decision, SqlFunctionAllowlists.Ansi, out _))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        Should.Throw<GatewaySecurityException>(() => DecisionRowFilter.TryResolveExpression(decision, SqlFunctionAllowlists.Ansi, out _))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        DecisionRowFilter.IsInlineEligiblePolicyPredicate(decision).ShouldBeFalse();
    }

    [Fact]
    public void DecisionRowFilter_TextOnlyBracketedAndParameterized_ParsesCleanly()
    {
        var decision = AllowedOrders("[region] = @p0 AND [status] = @p1");

        DecisionRowFilter.TryResolveTypedPredicate(decision, out _).ShouldBeFalse();
        DecisionRowFilter.IsInlineEligiblePolicyPredicate(decision).ShouldBeFalse();

        DecisionRowFilter.TryResolve(decision, SqlFunctionAllowlists.Ansi, out var resolved).ShouldBeTrue();
        resolved.IsTyped.ShouldBeFalse();
        resolved.TypedPredicate.ShouldBeNull();
        var andExpr = resolved.Expression.ShouldBeOfType<BinaryExpression>();
        andExpr.Operator.ShouldBe(BinaryOperator.And);
        var leftEq = andExpr.Left.ShouldBeOfType<BinaryExpression>();
        leftEq.Left.ShouldBeOfType<ColumnReference>().Name.SimpleName.ShouldBe("region");
        var paramRef = leftEq.Right.ShouldBeOfType<ParameterReference>();
        paramRef.IsSynthetic.ShouldBeTrue();
        paramRef.Name.ShouldBe("p0");
    }

    [Theory]
    [InlineData("region = 'EU' -- trailing comment")]
    [InlineData("region = 'EU' /* block */")]
    [InlineData("region = '${env:SECRET}'")]
    [InlineData("region = $$EU$$")]
    [InlineData("region = 'EU'; DROP TABLE orders")]
    [InlineData("xp_cmdshell('whoami') = 1")]
    [InlineData("convert_timezone('UTC', region) = 'EU'")]
    [InlineData("__param_x = 'EU'")]
    [InlineData("[region = 'EU'")]
    [InlineData("r\u00E8gion = 'EU'")]
    public void DecisionRowFilter_TextOnlyInvalidOrDisallowed_Rejects(string unsafeFilter)
    {
        var decision = AllowedOrders(unsafeFilter);

        Should.Throw<Exception>(() =>
            DecisionRowFilter.TryResolve(
                decision,
                SqlFunctionAllowlists.BuildForPolicy(TargetSqlDialect.PostgreSql, null),
                out _));
    }

    [Fact]
    public void DecisionRowFilter_NullAndCancellation_FailPredictably()
    {
        Should.Throw<ArgumentNullException>(() => DecisionRowFilter.TryResolveTypedPredicate(null!, out _));
        Should.Throw<ArgumentNullException>(() => DecisionRowFilter.TryResolve(null!, SqlFunctionAllowlists.Ansi, out _));
        Should.Throw<ArgumentNullException>(() => DecisionRowFilter.IsInlineEligiblePolicyPredicate(null!));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Should.Throw<OperationCanceledException>(() =>
            DecisionRowFilter.TryResolve(AllowedOrders("region = 'EU'"), SqlFunctionAllowlists.Ansi, out _, ct: cts.Token));
    }

    [Fact]
    public void SubtreeCompiler_TypedPolicy_PreservesParameters_AndFailsClosedOnDriftOrUnknown()
    {
        var sql = "SELECT o.id FROM pg.public.orders o JOIN pg.public.order_items oi ON o.id = oi.order_id";
        var analysis = Engine.Analyze(sql.AsMemory());
        var ordersMeta = OrdersMeta();
        var itemsMeta = ItemsMeta();
        var tables = new List<ResolvedSourceTable>
        {
            new(analysis.ReferencedTables[0], ordersMeta.Identifier, ordersMeta, "pg"),
            new(analysis.ReferencedTables[1], itemsMeta.Identifier, itemsMeta, "pg")
        };
        var (tree, _) = Engine.Parse(sql.AsMemory());
        var subtree = SubtreeGraphPartitioner.Partition(
            new SqlAstBuilder(new AstBuilderOptions { AllowedFunctions = SqlFunctionAllowlists.DuckDb }).BuildStatement(tree),
            tables,
            12).Subtrees.Single();

        var typedDecision = AllowedOrders().WithAdditionalRowPolicy(TypedRegionPolicy("EU"));
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [ordersMeta.Identifier] = typedDecision,
            [itemsMeta.Identifier] = TableAccessDecision.Allowed(itemsMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var plan = SubtreeCompiler.Compile(subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t1"));
        plan.Parameters.ShouldNotBeNull();
        plan.Parameters.Values.ShouldContain("EU");

        var driftedDecisions = new Dictionary<TableIdentifier, TableAccessDecision>(decisions)
        {
            [ordersMeta.Identifier] = typedDecision with { CombinedRowFilterSql = "region = 'US'" }
        };
        Should.Throw<GatewaySecurityException>(() =>
            SubtreeCompiler.Compile(subtree, driftedDecisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t1")))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);

        var unknownDecisions = new Dictionary<TableIdentifier, TableAccessDecision>(decisions)
        {
            [ordersMeta.Identifier] = AllowedOrders() with { RowPolicy = new UnknownRowPolicy(), CombinedRowFilterSql = null }
        };
        Should.Throw<GatewaySecurityException>(() =>
            SubtreeCompiler.Compile(subtree, unknownDecisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t1")))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);

        var otherTable = new ResolvedSourceTable(
            new TableAccessTarget("other_db", "dbo", "other", "ot", "other_db.dbo.other"),
            new TableIdentifier("crm", "dbo", "other"),
            OrdersMeta() with { Identifier = new TableIdentifier("crm", "dbo", "other"), Table = new Table { SourceName = "crm", SchemaName = "dbo", TableName = "other", PhysicalDatabaseName = "db_crm" } },
            "crm");
        Should.Throw<GatewaySecurityException>(() => SubtreeCompiler.ValidatePolicyIsolation([tables[0], otherTable], driftedDecisions))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
        Should.Throw<GatewaySecurityException>(() => SubtreeCompiler.ValidatePolicyIsolation([tables[0], otherTable], unknownDecisions))
            .ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    public void MetricViewStatementGuard_TypedAndTextFilters_FailClosedOnMeasuresDriftAndUnknown()
    {
        var mv = MetricViewMeta();
        var clearAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = ColumnAccessLevel.Clear,
            ["order_date"] = ColumnAccessLevel.Clear,
            ["revenue"] = ColumnAccessLevel.Clear
        };
        const string query = "SELECT region, MEASURE(revenue) FROM analytics.sales_mv GROUP BY region";

        var typedDim = TableAccessDecision.Allowed(mv.Identifier, clearAccess).WithAdditionalRowPolicy(TypedRegionPolicy("EU", TargetSqlDialect.Databricks));
        Should.NotThrow(() => MetricViewStatementGuard.EnforceForSingleTable(query, mv, typedDim, DatabaseDialect.Databricks));

        var bracketedText = TableAccessDecision.Allowed(mv.Identifier, clearAccess, rowFilterSql: "[region] = @p0");
        Should.NotThrow(() => MetricViewStatementGuard.EnforceForSingleTable(query, mv, bracketedText, DatabaseDialect.Databricks));

        var typedMeasure = TableAccessDecision.Allowed(mv.Identifier, clearAccess).WithAdditionalRowPolicy(TypedMeasurePolicy());
        Should.Throw<WebSqlPolicyException>(() => MetricViewStatementGuard.EnforceForSingleTable(query, mv, typedMeasure, DatabaseDialect.Databricks))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);

        var drifted = typedDim with { CombinedRowFilterSql = "region = 'US'" };
        Should.Throw<WebSqlPolicyException>(() => MetricViewStatementGuard.EnforceForSingleTable(query, mv, drifted, DatabaseDialect.Databricks))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);

        var unknown = TableAccessDecision.Allowed(mv.Identifier, clearAccess) with { RowPolicy = new UnknownRowPolicy(), CombinedRowFilterSql = null };
        Should.Throw<WebSqlPolicyException>(() => MetricViewStatementGuard.EnforceForSingleTable(query, mv, unknown, DatabaseDialect.Databricks))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);

        var disallowedFn = TableAccessDecision.Allowed(mv.Identifier, clearAccess, rowFilterSql: "xp_cmdshell(region) = 1");
        Should.Throw<WebSqlPolicyException>(() => MetricViewStatementGuard.EnforceForSingleTable(query, mv, disallowedFn, DatabaseDialect.Databricks))
            .Message.ShouldBe(MetricViewStatementGuard.RefusalMessage);
    }

    [Fact]
    public void TreeSqlCompiler_DeniesSubqueryDriftedUnknownAndUntypedWithStableCode()
    {
        var meta = OrdersMeta();
        var root = new TreeQueryNode(OrdersId, ["id"]);

        var inlineDecision = AllowedOrders().WithAdditionalRowPolicy(TypedRegionPolicy("EU"));
        TreeSqlCompiler.RowPolicyRequiresEnginePipeline(inlineDecision).ShouldBeFalse();
        TreeSqlCompiler.Compile(root, new Dictionary<TableIdentifier, TreeTableAccess> { [OrdersId] = new(meta, inlineDecision) }, DatabaseDialect.PostgreSql)
            .Parameters.Values.ShouldContain("EU");

        var subqueryDecision = AllowedOrders().WithAdditionalRowPolicy(TypedSubqueryPolicy());
        var driftedDecision = inlineDecision with { CombinedRowFilterSql = "region = 'US'" };
        var unknownWithText = AllowedOrders("region = 'EU'") with { RowPolicy = new UnknownRowPolicy() };
        var unknownWithoutText = AllowedOrders() with { RowPolicy = new UnknownRowPolicy(), CombinedRowFilterSql = null };
        var textOnlyDecision = AllowedOrders("region = 'EU'");

        foreach (var unsupported in new[] { subqueryDecision, driftedDecision, unknownWithText, unknownWithoutText, textOnlyDecision })
        {
            TreeSqlCompiler.RowPolicyRequiresEnginePipeline(unsupported).ShouldBeTrue();
            var access = new Dictionary<TableIdentifier, TreeTableAccess> { [OrdersId] = new(meta, unsupported) };
            var ex = Should.Throw<GatewaySecurityException>(() => TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql));
            ex.ErrorCode.ShouldBe("TREE_ROW_POLICY_UNSUPPORTED");
        }
    }

    [Fact]
    public void RequiredFilterColumnRule_DriftedOrUnknownPolicySatisfiesNothing()
    {
        var meta = OrdersMeta(["region"]);

        var validTyped = AllowedOrders().WithAdditionalRowPolicy(TypedRegionPolicy("EU"));
        RequiredFilterColumnRule.IsSatisfied(meta, validTyped, clientConstrainedColumns: null, tenantColumnCounts: false).ShouldBeTrue();

        // Drifted CombinedRowFilterSql naming 'region' while typed policy is on 'status' must satisfy NOTHING.
        var namer = new PolicyParameterNamer("__rf_");
        var statusParam = namer.Add(new PolicyValue("open", SqlParameterType.String));
        var statusPolicy = new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new BinaryExpression(PolicyParameterNamer.Column("status"), BinaryOperator.Equal, statusParam), namer.Values),
            TargetSqlDialect.PostgreSql,
            Renderer);
        var drifted = AllowedOrders().WithAdditionalRowPolicy(statusPolicy) with { CombinedRowFilterSql = "region = 'EU'" };
        RequiredFilterColumnRule.IsSatisfied(meta, drifted, clientConstrainedColumns: null, tenantColumnCounts: false).ShouldBeFalse();
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            RequiredFilterColumnRule.Enforce(meta, drifted, clientConstrainedColumns: null, tenantColumnCounts: false));

        // Unknown IRowPolicy with CombinedRowFilterSql naming 'region' must satisfy NOTHING.
        var unknown = AllowedOrders("region = 'EU'") with { RowPolicy = new UnknownRowPolicy() };
        RequiredFilterColumnRule.IsSatisfied(meta, unknown, clientConstrainedColumns: null, tenantColumnCounts: false).ShouldBeFalse();

        // Text-only (RowPolicy is null) still checks referenced identifiers in CombinedRowFilterSql.
        var textOnly = AllowedOrders("region = 'EU'");
        RequiredFilterColumnRule.IsSatisfied(meta, textOnly, clientConstrainedColumns: null, tenantColumnCounts: false).ShouldBeTrue();
    }

    private sealed class UnknownRowPolicy(string sql = "region = 'EU'") : IRowPolicy
    {
        public string Fingerprint => "unknown-fp";
        public bool IsDenyAll => false;
        public IRowPolicy And(IRowPolicy other) => throw new NotSupportedException();
        public RowPolicyText Project() => new(sql, new Dictionary<string, object?>());
    }
}
