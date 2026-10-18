namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class WebSqlFilterPushdownPlannerTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateTable(
        string domain,
        string table,
        DataSourceType dsType,
        IReadOnlyList<string> columns,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        Dictionary<string, MaskingRule>? masking = null,
        Dictionary<string, string>? columnMeta = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", table),
            Table = new Table
            {
                TableName = table,
                SchemaName = "public",
                SourceType = dsType == DataSourceType.Sql ? dialect.ToString() : "Http",
                DataSourceType = dsType,
                SourceName = domain,
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn
            {
                ColumnName = c,
                DataType = "varchar",
                Meta = columnMeta != null ? new Dictionary<string, string>(columnMeta) : new Dictionary<string, string>()
            }).ToList(),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        };
    }

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder().BuildStatement(tree);
    }

    [Fact]
    public void FlattenAnd_FlattensDeeplyNestedAndExpressions()
    {
        var sql = "SELECT * FROM t WHERE (a = 1 AND (b = 2 AND c = 3))";
        var statement = Parse(sql);
        var select = (SelectStatement)statement;
        var querySpec = (QuerySpecification)select.Body;

        var collector = new List<Expression>();
        SqlConjunctSplitter.FlattenAnd(querySpec.Where!, collector);

        collector.Count.ShouldBe(3);
    }

    [Fact]
    public void FlattenAnd_PreservesSingleTableOrAsSingleConjunct()
    {
        var sql = "SELECT * FROM t WHERE (c.status = 'A' OR c.status = 'B') AND c.active = 1";
        var statement = Parse(sql);
        var select = (SelectStatement)statement;
        var querySpec = (QuerySpecification)select.Body;

        var collector = new List<Expression>();
        SqlConjunctSplitter.FlattenAnd(querySpec.Where!, collector);

        collector.Count.ShouldBe(2);
        collector[0].ShouldBeOfType<BinaryExpression>().Operator.ShouldBe(BinaryOperator.Or);
        collector[1].ShouldBeOfType<BinaryExpression>().Operator.ShouldBe(BinaryOperator.Equal);
    }

    [Fact]
    public void Split_HonorsLeftOuterJoinSemantics()
    {
        var sql = "SELECT * FROM orders o LEFT JOIN customers c ON o.customer_id = c.id AND c.status = 'ACTIVE' WHERE o.total > 100 AND c.name = 'John'";
        var statement = Parse(sql);

        var conjuncts = SqlConjunctSplitter.Split(statement);

        // Conjuncts:
        // 1. o.customer_id = c.id (ON) -> allowed on customers
        // 2. c.status = 'ACTIVE' (ON) -> allowed on customers
        // 3. o.total > 100 (WHERE) -> allowed on orders
        // 4. c.name = 'John' (WHERE) -> allowed only on orders (customers is null-supplying)
        conjuncts.Count.ShouldBe(4);

        var onConjuncts = conjuncts.Where(c => c.Origin == ConjunctOrigin.LeftOuterJoinOn).ToList();
        onConjuncts.Count.ShouldBe(2);

        var whereConjuncts = conjuncts.Where(c => c.Origin == ConjunctOrigin.Where).ToList();
        whereConjuncts.Count.ShouldBe(2);
    }

    [Fact]
    public void Split_FullOuterJoin_ExcludesAllPushdown()
    {
        var sql = "SELECT * FROM orders o FULL OUTER JOIN customers c ON o.customer_id = c.id WHERE o.total > 100";
        var statement = Parse(sql);

        var conjuncts = SqlConjunctSplitter.Split(statement);

        conjuncts.ShouldBeEmpty();
    }

    [Fact]
    public void Classify_SingleTableAndMultiTable()
    {
        var sql = "SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id WHERE (o.total > 100 AND o.status = 'PAID') AND (o.id = 1 OR c.id = 2)";
        var statement = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "customer_id", "total", "status"]);
        var customerMeta = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "name", "status"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tCustomers = new ResolvedSourceTable(metadata.ReferencedTables[1], customerMeta.Identifier, customerMeta, "crm");

        var conjuncts = SqlConjunctSplitter.Split(statement);
        var result = SingleTableConjunctClassifier.Classify(conjuncts, [tOrders, tCustomers]);

        // Single-table conjuncts: o.total > 100, o.status = 'PAID'
        result.SingleTableConjuncts.Count.ShouldBe(2);
        result.SingleTableConjuncts.All(c => c.Table == orderMeta.Identifier).ShouldBeTrue();

        // Multi-table conjuncts: o.customer_id = c.id, (o.id = 1 OR c.id = 2)
        result.MultiTableConjuncts.Count.ShouldBe(2);
    }

    [Fact]
    public void PushabilityEvaluator_RejectsMaskedAndHmacColumns()
    {
        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "secret_token", "amount"]);
        var decision = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["secret_token"] = ColumnAccessLevel.Mask,
            ["amount"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var sql = "SELECT * FROM orders WHERE secret_token = 'abc'";
        var statement = Parse(sql);
        var select = (SelectStatement)statement;
        var querySpec = (QuerySpecification)select.Body;

        var conjunct = new SingleTableConjunct(
            orderMeta.Identifier,
            querySpec.Where!,
            ["secret_token"],
            new ExtractedConjunct(querySpec.Where!, ConjunctOrigin.Where));

        var eval = PushabilityEvaluator.Evaluate(
            conjunct,
            orderMeta,
            decision,
            DatabaseDialect.PostgreSql,
            vfPlan: null,
            new PredicatePushdownOptions());

        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.MaskedColumn);
    }

    [Fact]
    public void PushabilityEvaluator_EnforcesCollationRiskOnSqlServerStrings()
    {
        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "code"], dialect: DatabaseDialect.SqlServer);
        var decision = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["code"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        // String inequality <> without exact_compare hint
        var sql = "SELECT * FROM orders WHERE code <> 'XYZ'";
        var statement = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)statement).Body;

        var conjunct = new SingleTableConjunct(
            orderMeta.Identifier,
            querySpec.Where!,
            ["code"],
            new ExtractedConjunct(querySpec.Where!, ConjunctOrigin.Where));

        var eval = PushabilityEvaluator.Evaluate(
            conjunct,
            orderMeta,
            decision,
            DatabaseDialect.SqlServer,
            vfPlan: null,
            new PredicatePushdownOptions());

        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.CollationRisk);

        // Equality is allowed (returns superset)
        var eqSql = "SELECT * FROM orders WHERE code = 'XYZ'";
        var eqStatement = Parse(eqSql);
        var eqConjunct = new SingleTableConjunct(
            orderMeta.Identifier,
            ((QuerySpecification)((SelectStatement)eqStatement).Body).Where!,
            ["code"],
            new ExtractedConjunct(((QuerySpecification)((SelectStatement)eqStatement).Body).Where!, ConjunctOrigin.Where));

        var eqEval = PushabilityEvaluator.Evaluate(
            eqConjunct,
            orderMeta,
            decision,
            DatabaseDialect.SqlServer,
            vfPlan: null,
            new PredicatePushdownOptions());

        eqEval.IsPushable.ShouldBeTrue();
    }

    [Fact]
    public void PushabilityEvaluator_EnforcesExactCompareHintOnSqlServer()
    {
        var orderMeta = CreateTable(
            "crm", "orders", DataSourceType.Sql, ["id", "code"],
            dialect: DatabaseDialect.SqlServer,
            columnMeta: new Dictionary<string, string> { ["exact_compare"] = "true" });

        var decision = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["code"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var sql = "SELECT * FROM orders WHERE code <> 'XYZ'";
        var statement = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)statement).Body;

        var conjunct = new SingleTableConjunct(
            orderMeta.Identifier,
            querySpec.Where!,
            ["code"],
            new ExtractedConjunct(querySpec.Where!, ConjunctOrigin.Where));

        var eval = PushabilityEvaluator.Evaluate(
            conjunct,
            orderMeta,
            decision,
            DatabaseDialect.SqlServer,
            vfPlan: null,
            new PredicatePushdownOptions());

        eval.IsPushable.ShouldBeTrue();
    }

    [Fact]
    public void PushabilityEvaluator_EnforcesThreeValuedLogicOnNotIn()
    {
        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "status"]);
        var decision = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["status"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var sql = "SELECT * FROM orders WHERE status NOT IN ('A', 'B', NULL)";
        var statement = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)statement).Body;

        var conjunct = new SingleTableConjunct(
            orderMeta.Identifier,
            querySpec.Where!,
            ["status"],
            new ExtractedConjunct(querySpec.Where!, ConjunctOrigin.Where));

        var eval = PushabilityEvaluator.Evaluate(
            conjunct,
            orderMeta,
            decision,
            DatabaseDialect.PostgreSql,
            vfPlan: null,
            new PredicatePushdownOptions());

        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.NullInNotInList);
    }

    [Fact]
    public void PushabilityEvaluator_RejectsHttpAndNonSqlSources()
    {
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id", "tracking"]);
        var decision = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["tracking"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var sql = "SELECT * FROM shipments WHERE tracking = 'TRK123'";
        var statement = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)statement).Body;

        var conjunct = new SingleTableConjunct(
            shipMeta.Identifier,
            querySpec.Where!,
            ["tracking"],
            new ExtractedConjunct(querySpec.Where!, ConjunctOrigin.Where));

        var eval = PushabilityEvaluator.Evaluate(
            conjunct,
            shipMeta,
            decision,
            DatabaseDialect.PostgreSql,
            vfPlan: null,
            new PredicatePushdownOptions());

        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.NotSqlSource);
    }

    [Fact]
    public void PushedClauseBuilder_BuildsParameterizedClauseWithTrinoFactory()
    {
        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "customer_id", "status"]);

        var sql = "SELECT * FROM orders WHERE customer_id = 42 AND status = 'ACTIVE'";
        var statement = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)statement).Body;

        var conjuncts = new List<Expression>();
        SqlConjunctSplitter.FlattenAnd(querySpec.Where!, conjuncts);

        var singleConjuncts = conjuncts.Select(c =>
        {
            var cols = new List<ColumnReference>();
            SingleTableConjunctClassifier.CollectColumnReferences(c, cols);
            return new SingleTableConjunct(
                orderMeta.Identifier,
                c,
                cols.Select(cr => cr.Name.Parts[^1].Value).ToList(),
                new ExtractedConjunct(c, ConjunctOrigin.Where));
        }).ToList();

        var clause = PushedClauseBuilder.Build(singleConjuncts, orderMeta);

        // Parameters must use @p_pd_<n> prefix
        clause.Parameters.Keys.ShouldContain("@p_pd_0");
        clause.Parameters.Keys.ShouldContain("@p_pd_1");
        clause.Parameters["@p_pd_0"].ShouldBe(42L);
        clause.Parameters["@p_pd_1"].ShouldBe("ACTIVE");

        // TrinoSqlFactory must render delimited identifiers
        var trinoSql = clause.GetTrinoPredicate();
        trinoSql.ShouldContain("\"customer_id\" = @p_pd_0");
        trinoSql.ShouldContain("\"status\" = @p_pd_1");

        // Structural predicates
        clause.PushdownPredicates.ShouldNotBeNull();
        clause.PushdownPredicates!.Count.ShouldBe(2);
        clause.PushdownPredicates[0].Column.ShouldBe("customer_id");
        clause.PushdownPredicates[0].Operator.ShouldBe("Equal");
        clause.PushdownPredicates[1].Column.ShouldBe("status");
        clause.PushdownPredicates[1].Operator.ShouldBe("Equal");
    }
}
