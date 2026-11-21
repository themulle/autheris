namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.Optimization;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
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
using Xunit;

using OptimizationClauseBuilder = Autheris.Application.Sql.Federation.Optimization.PushedClauseBuilder;
using PushdownClauseBuilder = Autheris.Application.Sql.Federation.Pushdown.PushedClauseBuilder;

/// <summary>Regression tests covering Issue #993 federation, subtree RLS, parameter binding, pushdown quoting, and CTE scoping correctness.</summary>
public sealed class FederationCorrectnessTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateTable(
        string domain,
        string schema,
        string table,
        DatabaseDialect dialect,
        IReadOnlyList<(string Name, string Type)> columns,
        string? physicalDb = null,
        string? tenantColumn = null) => new()
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table
            {
                TableName = table,
                SchemaName = schema,
                SourceType = dialect.ToString(),
                DataSourceType = DataSourceType.Sql,
                SourceName = domain,
                PhysicalDatabaseName = physicalDb,
                IsActive = true
            },
            TenantColumnName = tenantColumn,
            Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

    private static TableAccessDecision Unrestricted(TableMetadata meta, string? rls = null, IReadOnlyDictionary<string, object?>? rlsParams = null) =>
        TableAccessDecision.Allowed(
            meta.Identifier,
            meta.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear),
            hasUnconstrainedColumnAllow: true) with
        {
            CombinedRowFilterSql = rls,
            RowFilterParameters = rlsParams
        };

    private static SqlStatement ParseSql(string sql, bool enforceFunctionPolicy = true)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder(new AstBuilderOptions
        {
            EnforceFunctionPolicy = enforceFunctionPolicy,
            AllowedFunctions = enforceFunctionPolicy
                ? SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.DuckDb)
                : null
        }).BuildStatement(tree);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Subtree_OrdinalParameterBinding_IgnoresQuotedMarkersAndLiterals()
    {
        var sql = "SELECT o.id, o.status " +
                  "FROM pg.public.orders o JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "WHERE o.status = 'OPEN' AND oi.tier = 'GOLD'";

        var orders = CreateTable("pg", "public", "orders", DatabaseDialect.PostgreSql, [("id", "int"), ("status", "varchar")], tenantColumn: "tenant_id");
        var items = CreateTable("pg", "public", "order_items", DatabaseDialect.PostgreSql, [("id", "int"), ("order_id", "int"), ("tier", "varchar")], tenantColumn: "tenant_id");
        var meta = Engine.Analyze(sql.AsMemory());
        var tables = new List<ResolvedSourceTable>
        {
            new(meta.ReferencedTables[0], orders.Identifier, orders, "pg"),
            new(meta.ReferencedTables[1], items.Identifier, items, "pg")
        };

        var baseSubtree = SubtreeGraphPartitioner.Partition(ParseSql(sql), tables, 12).Subtrees.Single();
        var subtree = baseSubtree with
        {
            ProjectedItems =
            [
                new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("o"), new SqlIdentifier("id")])), new SqlIdentifier("a$b", IsQuoted: true)),
                new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("o"), new SqlIdentifier("status")])), new SqlIdentifier("@p", IsQuoted: true)),
                new ColumnSelectItem(new LiteralExpression("@x", LiteralType.String), new SqlIdentifier("lit", IsQuoted: false))
            ]
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orders.Identifier] = Unrestricted(orders),
            [items.Identifier] = Unrestricted(items)
        };

        // PostgreSQL ($1, $2) AND SQL Server (@p0, @p1) dialects must keep quoted identifiers/literals intact and bind strictly by ordinal
        foreach (var dialect in new[] { TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer })
        {
            var plan = SubtreeCompiler.Compile(
                subtree with { Dialect = dialect },
                decisions,
                new CrossSourceOptions { Enabled = true },
                Engine,
                PlannerTestIdentity.User,
                new TenantId("tenant-pg"));

            string openQuote = dialect == TargetSqlDialect.SqlServer ? "[" : "\"";
            string closeQuote = dialect == TargetSqlDialect.SqlServer ? "]" : "\"";
            plan.Sql.ShouldContain($"{openQuote}a$b{closeQuote}");
            plan.Sql.ShouldContain($"{openQuote}@p{closeQuote}");

            var boundParams = plan.Parameters.ShouldNotBeNull();
            boundParams.Count.ShouldBe(plan.Compiled.Parameters.Length);
            for (int i = 0; i < plan.Compiled.Parameters.Length; i++)
            {
                boundParams[$"@p_st0_{i}"].ShouldBe(plan.Compiled.Parameters[i].Value);
            }

            boundParams.Values.ShouldNotContain((object?)null);
            boundParams.Values.ShouldContain("@x");
            boundParams.Values.ShouldContain("OPEN");
            boundParams.Values.ShouldContain("GOLD");
            boundParams.Values.ShouldContain("tenant-pg");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Subtree_OuterJoins_WithNullableSideAndAmbiguousRlsColumns_PreserveOuterJoinNullExtension()
    {
        var customers = CreateTable("dk", "public", "customers", DatabaseDialect.PostgreSql, [("cust_id", "int"), ("region", "varchar")]);
        var orders = CreateTable("dk", "public", "orders", DatabaseDialect.PostgreSql, [("order_id", "int"), ("customer_id", "int"), ("region", "varchar")]);

        foreach (var (joinKeyword, expectedAstJoin, expectedSqlKeyword) in new[]
        {
            ("LEFT JOIN", JoinType.LeftOuter, "LEFT OUTER JOIN"),
            ("RIGHT JOIN", JoinType.RightOuter, "RIGHT OUTER JOIN"),
            ("FULL OUTER JOIN", JoinType.FullOuter, "FULL OUTER JOIN")
        })
        {
            string sql = $"SELECT c.cust_id, o.order_id FROM dk.public.customers c {joinKeyword} dk.public.orders o ON c.cust_id = o.customer_id ORDER BY c.cust_id, o.order_id";
            var meta = Engine.Analyze(sql.AsMemory());
            var tables = new List<ResolvedSourceTable>
            {
                new(meta.ReferencedTables[0], customers.Identifier, customers, "dk"),
                new(meta.ReferencedTables[1], orders.Identifier, orders, "dk")
            };

            var subtree = SubtreeGraphPartitioner.Partition(ParseSql(sql), tables, 12).Subtrees.Single() with
            {
                Dialect = TargetSqlDialect.DuckDb
            };
            var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
            {
                [customers.Identifier] = Unrestricted(customers, rls: "region = 'US'"),
                [orders.Identifier] = Unrestricted(orders, rls: "region = 'EU'")
            };

            var plan = SubtreeCompiler.Compile(subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t-outer"));

            // Member row filters must be scoped inside each derived subquery side of the outer join, never lifted to top-level WHERE
            plan.Sql.ShouldContain(expectedSqlKeyword);
            var reparsedSql = plan.Sql.Replace("@p_st0_", "__param_p_st0_", StringComparison.Ordinal);
            var compiledAst = (SelectStatement)ParseSql(reparsedSql, enforceFunctionPolicy: false);
            var outerSpec = compiledAst.Body.ShouldBeOfType<QuerySpecification>();
            outerSpec.Where.ShouldBeNull();

            var joined = outerSpec.From.ShouldBeOfType<JoinedTableSource>();
            joined.Type.ShouldBe(expectedAstJoin);
            var leftSub = joined.Left.ShouldBeOfType<SubqueryTableSource>();
            var rightSub = joined.Right.ShouldBeOfType<SubqueryTableSource>();

            var leftSpec = leftSub.Subquery.Body.ShouldBeOfType<QuerySpecification>();
            var rightSpec = rightSub.Subquery.Body.ShouldBeOfType<QuerySpecification>();
            leftSpec.Where.ShouldNotBeNull();
            rightSpec.Where.ShouldNotBeNull();

            // Unqualified ambiguous column 'region' is isolated cleanly to autheris_target on each derived subquery
            plan.Parameters.ShouldNotBeNull();
            plan.Parameters.Values.ShouldContain("US");
            plan.Parameters.Values.ShouldContain("EU");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Subtree_RowFilterParameters_NormalizedAndBoundPerMember_FailsClosedWhenMissing()
    {
        var sql = "SELECT c.cust_id, o.order_id FROM dk.public.customers c JOIN dk.public.orders o ON c.cust_id = o.customer_id";
        var customers = CreateTable("dk", "public", "customers", DatabaseDialect.PostgreSql, [("cust_id", "int"), ("region", "varchar")]);
        var orders = CreateTable("dk", "public", "orders", DatabaseDialect.PostgreSql, [("order_id", "int"), ("customer_id", "int"), ("tier", "varchar")]);
        var meta = Engine.Analyze(sql.AsMemory());
        var tables = new List<ResolvedSourceTable>
        {
            new(meta.ReferencedTables[0], customers.Identifier, customers, "dk"),
            new(meta.ReferencedTables[1], orders.Identifier, orders, "dk")
        };

        var subtree = SubtreeGraphPartitioner.Partition(ParseSql(sql), tables, 12).Subtrees.Single() with
        {
            Dialect = TargetSqlDialect.DuckDb
        };
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [customers.Identifier] = Unrestricted(customers, rls: "region = @__gql_rls_0",
                rlsParams: new Dictionary<string, object?> { ["@__gql_rls_0"] = "EU" }),
            [orders.Identifier] = Unrestricted(orders, rls: "tier = @__gql_rls_0 AND order_id > @min_id",
                rlsParams: new Dictionary<string, object?> { ["@__gql_rls_0"] = "GOLD", ["min_id"] = 100 })
        };

        var plan = SubtreeCompiler.Compile(subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t-param"));
        var boundParams = plan.Parameters.ShouldNotBeNull();
        boundParams.Count.ShouldBe(3);
        boundParams.Values.ShouldContain("EU");
        boundParams.Values.ShouldContain("GOLD");
        boundParams.Values.ShouldContain(100);
        plan.Sql.ShouldContain("@p_st0_0");
        plan.Sql.ShouldContain("@p_st0_1");
        plan.Sql.ShouldContain("@p_st0_2");
        plan.Sql.ShouldNotContain("__param_");
        plan.Sql.ShouldNotContain("@__gql_rls_0");

        var missingDecisions = new Dictionary<TableIdentifier, TableAccessDecision>(decisions)
        {
            [orders.Identifier] = Unrestricted(orders, rls: "tier = @missing_param")
        };
        var ex = Should.Throw<GatewaySecurityException>(() =>
            SubtreeCompiler.Compile(subtree, missingDecisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t-param")));
        ex.ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    public void PushedClauseBuilder_ResolvesCatalogCasing_AndDoublesEmbeddedQuotes()
    {
        var meta = CreateTable("crm", "public", "orders", DatabaseDialect.PostgreSql,
            [("Weird\"Col", "varchar"), ("MixedCaseCol", "varchar")]);
        var decision = Unrestricted(meta);

        var stmt = ParseSql("SELECT * FROM crm.public.orders WHERE \"weird\"\"col\" = 'v1' AND mixedcasecol = 'v2'");
        var conjuncts = SqlConjunctSplitter.Split(stmt)
            .Select(c => new SingleTableConjunct(meta.Identifier, c.Expression, ["weird\"col", "mixedcasecol"], c))
            .ToList();

        var fromPushdown = PushdownClauseBuilder.Build(conjuncts, meta);
        fromPushdown.SqlPredicate.ShouldContain("\"Weird\"\"Col\" = @p_pd_0");
        fromPushdown.SqlPredicate.ShouldContain("\"MixedCaseCol\" = @p_pd_1");
        fromPushdown.ReferencedColumns.ShouldBe(["Weird\"Col", "MixedCaseCol"], ignoreOrder: true);

        var contract = FederatedParameterContract.Create(new Dictionary<string, object?>
        {
            ["p1"] = "val1",
            ["p2"] = "val2"
        });
        var candidates = new List<PushedConjunctCandidate>
        {
            new("weird\"col", "=", "p1", IsList: false),
            new("mixedcasecol", "=", "p2", IsList: false)
        };

        var built = PushdownClauseBuilder.BuildClause(meta, decision, candidates, contract, null, out var audit);
        var forwarded = OptimizationClauseBuilder.BuildClause(meta, decision, candidates, contract, null, out _);
        built.ShouldNotBeNull();
        forwarded.ShouldNotBeNull();
        built.SqlPredicate.ShouldBe("\"Weird\"\"Col\" = @p_pd_0_0 AND \"MixedCaseCol\" = @p_pd_1_0");
        forwarded.SqlPredicate.ShouldBe(built.SqlPredicate);
        built.ConstrainedColumns.ShouldBe(["Weird\"Col", "MixedCaseCol"], ignoreOrder: true);
        audit.ShouldNotBeNull();
        audit.ParameterNames.ShouldBe(["p1", "p2"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CrossSourcePlanner_ScopedCteStack_PreventsInnerCteFromSuppressingOuterOrSiblingTables()
    {
        var sql = "SELECT sub.id, o.amount FROM (" +
                  "WITH orders AS (SELECT 1 AS id) SELECT id FROM orders" +
                  ") sub " +
                  "JOIN pg.public.orders o ON sub.id = o.id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.order_id";

        var orders = CreateTable("pg", "public", "orders", DatabaseDialect.PostgreSql, [("id", "int"), ("amount", "int")]);
        var shipments = CreateTable("ms", "dbo", "shipments", DatabaseDialect.SqlServer, [("id", "int"), ("order_id", "int")]);
        var meta = Engine.Analyze(sql.AsMemory());
        var byFullName = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["pg.public.orders"] = orders,
            ["ms.dbo.shipments"] = shipments
        };
        var tables = meta.ReferencedTables.Select(t => new ResolvedSourceTable(t, byFullName[t.FullName].Identifier, byFullName[t.FullName], byFullName[t.FullName].Identifier.Domain)).ToList();
        var decisions = tables.Select(t => t.Metadata).DistinctBy(m => m.Identifier)
            .ToDictionary(m => m.Identifier, m => Unrestricted(m));

        var plan = new CrossSourcePlanner(Engine).Plan(
            sql, meta, tables, decisions, new CrossSourceOptions { Enabled = true }, PlannerTestIdentity.User, PlannerTestIdentity.Tenant);

        plan.GeneratedDuckDbSql.ShouldContain("FROM \"orders\"");
        plan.GeneratedDuckDbSql.ShouldContain("\"s0\"");
        plan.GeneratedDuckDbSql.ShouldContain("\"s1\"");

        // Verify simple-named sibling table after closed subquery CTE is rewritten (not masked by leaked CTE scope)
        var siblingSql = "SELECT a.id, o.amount FROM (WITH orders AS (SELECT 1 AS id) SELECT id FROM orders) a JOIN orders o ON a.id = o.id JOIN ms.dbo.shipments s ON o.id = s.order_id";
        var siblingStmt = ParseSql(siblingSql);
        var siblingMeta = Engine.Analyze(siblingSql.AsMemory());
        var siblingTables = new List<ResolvedSourceTable>
        {
            new(siblingMeta.ReferencedTables[0], orders.Identifier, orders, "pg"),
            new(siblingMeta.ReferencedTables[1], shipments.Identifier, shipments, "ms")
        };
        var siblingPlan = new CrossSourcePlanner(Engine).Plan(
            siblingSql, siblingMeta, siblingTables, decisions, new CrossSourceOptions { Enabled = true }, PlannerTestIdentity.User, PlannerTestIdentity.Tenant, statement: siblingStmt);

        siblingPlan.GeneratedDuckDbSql.ShouldContain("FROM \"orders\"");
        siblingPlan.GeneratedDuckDbSql.ShouldContain("JOIN \"s0\" AS \"o\"");

        // Recursive CTE self-reference stays unrewritten inside its recursive branch
        var recSql = "WITH RECURSIVE r AS (SELECT id FROM pg.public.orders UNION ALL SELECT id + 1 FROM r WHERE id < 3) " +
                     "SELECT r.id, s.order_id FROM r JOIN ms.dbo.shipments s ON r.id = s.order_id";
        var recMeta = Engine.Analyze(recSql.AsMemory());
        var recTables = new List<ResolvedSourceTable>
        {
            new(recMeta.ReferencedTables[0], orders.Identifier, orders, "pg"),
            new(recMeta.ReferencedTables[1], shipments.Identifier, shipments, "ms")
        };
        var recPlan = new CrossSourcePlanner(Engine).Plan(
            recSql, recMeta, recTables, decisions, new CrossSourceOptions { Enabled = true }, PlannerTestIdentity.User, PlannerTestIdentity.Tenant);
        recPlan.GeneratedDuckDbSql.ShouldContain("WITH RECURSIVE \"r\"");
        recPlan.GeneratedDuckDbSql.ShouldContain("FROM \"r\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SubtreeCompiler_IsolateCompiledParameters_FailsClosedOnMarkerDivergence_AndHandlesDedupedMarkers()
    {
        var method = typeof(SubtreeCompiler).GetMethod("IsolateCompiledParameters", BindingFlags.NonPublic | BindingFlags.Static)!;
        static (string Sql, Dictionary<string, object?> Parameters) InvokeIsolate(MethodInfo m, CompiledSql c)
        {
            try
            {
                return ((string, Dictionary<string, object?>))m.Invoke(null, [c, "st0", TargetSqlDialect.PostgreSql])!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        // Deduped repeated marker $1 emitted twice maps to single @p_st0_0 preserving ordinal alignment
        var deduped = new CompiledSql(
            "SELECT * FROM t WHERE a = $1 AND b = $1 AND c = $2",
            [
                new BoundParameter("$1", "1", 0, "tenant-a", SqlParameterType.String, ParameterOrigin.Tenant, "tenant"),
                new BoundParameter("$2", "2", 1, "active", SqlParameterType.String, ParameterOrigin.QueryLiteral, null)
            ],
            TargetSqlDialect.PostgreSql, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "test");
        var (isoSql, isoParams) = InvokeIsolate(method, deduped);
        isoSql.ShouldBe("SELECT * FROM t WHERE a = @p_st0_0 AND b = @p_st0_0 AND c = @p_st0_1");
        isoParams["@p_st0_0"].ShouldBe("tenant-a");
        isoParams["@p_st0_1"].ShouldBe("active");

        // Missing marker in SQL (Compiled.Parameters count > markers found) fails closed
        var missingMarker = deduped with { Sql = "SELECT * FROM t WHERE a = $1" };
        Should.Throw<GatewaySecurityException>(() => InvokeIsolate(method, missingMarker)).ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);

        // Stray unmapped marker in SQL fails closed
        var strayMarker = deduped with { Sql = "SELECT * FROM t WHERE a = $1 AND b = $2 AND c = $9" };
        Should.Throw<GatewaySecurityException>(() => InvokeIsolate(method, strayMarker)).ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SubtreeCompiler_ValidatePolicyIsolation_UsesExactIdentifierMatching()
    {
        var orders = CreateTable("src_sales", "dbo", "Orders", DatabaseDialect.SqlServer, [("id", "int"), ("status", "varchar"), ("action_id", "int")], physicalDb: "DB_Sales");
        var actTable = CreateTable("src_crm", "dbo", "act", DatabaseDialect.SqlServer, [("id", "int"), ("flag", "varchar")], physicalDb: "DB_CRM");

        var tOrders = new ResolvedSourceTable(new TableAccessTarget("DB_Sales", "dbo", "Orders", "o", "DB_Sales.dbo.Orders"), orders.Identifier, orders, "src_sales");
        var tAct = new ResolvedSourceTable(new TableAccessTarget("DB_CRM", "dbo", "act", "a", "DB_CRM.dbo.act"), actTable.Identifier, actTable, "src_crm");

        var safeDecisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orders.Identifier] = Unrestricted(orders, rls: "status = 'act' AND action_id > 0"),
            [actTable.Identifier] = Unrestricted(actTable)
        };
        Should.NotThrow(() => SubtreeCompiler.ValidatePolicyIsolation([tOrders, tAct], safeDecisions));

        var badDecisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orders.Identifier] = Unrestricted(orders, rls: "o.id IN (SELECT id FROM DB_CRM.dbo.act)"),
            [actTable.Identifier] = Unrestricted(actTable)
        };
        var ex = Should.Throw<GatewaySecurityException>(() => SubtreeCompiler.ValidatePolicyIsolation([tOrders, tAct], badDecisions));
        ex.ErrorCode.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SubtreeCompiler_CollectFunctionCalls_RejectsSchemaQualifiedFunctionsAndExcessiveDepth()
    {
        var orders = CreateTable("pg", "public", "orders", DatabaseDialect.PostgreSql, [("id", "int"), ("status", "varchar")]);
        var items = CreateTable("pg", "public", "order_items", DatabaseDialect.PostgreSql, [("id", "int"), ("order_id", "int")]);
        var sql = "SELECT o.id FROM pg.public.orders o JOIN pg.public.order_items oi ON o.id = oi.order_id WHERE evil_schema.lower(o.status) = 'x'";
        var meta = Engine.Analyze(sql.AsMemory());
        var tables = new List<ResolvedSourceTable>
        {
            new(meta.ReferencedTables[0], orders.Identifier, orders, "pg"),
            new(meta.ReferencedTables[1], items.Identifier, items, "pg")
        };

        var subtree = SubtreeGraphPartitioner.Partition(ParseSql(sql, enforceFunctionPolicy: false), tables, 12).Subtrees.Single();
        var decisions = tables.ToDictionary(t => t.Metadata.Identifier, t => Unrestricted(t.Metadata));

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            SubtreeCompiler.Compile(subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, PlannerTestIdentity.Tenant));
        ex.Message.ShouldContain("evil_schema.lower");

        Expression deepExpr = new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("o"), new SqlIdentifier("id")])),
            BinaryOperator.Equal,
            new LiteralExpression(1L, LiteralType.Integer));
        for (int i = 0; i < 140; i++)
        {
            deepExpr = new ParenthesizedExpression(deepExpr);
        }

        var deepSubtree = subtree with
        {
            ProjectedItems = [new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("o"), new SqlIdentifier("id")])), null)],
            PushablePredicates = [deepExpr]
        };

        var depthEx = Should.Throw<WebSqlPolicyException>(() =>
            SubtreeCompiler.Compile(deepSubtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, PlannerTestIdentity.Tenant));
        depthEx.Message.ShouldContain("depth");
    }
}
