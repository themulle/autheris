namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Services;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class WebSqlFilterPushdownSecurityTests
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

    private static SingleTableConjunct CreateSingleTableConjunct(string sql, TableMetadata meta, params string[] columns)
    {
        var stmt = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)stmt).Body;
        return new SingleTableConjunct(
            meta.Identifier,
            querySpec.Where!,
            columns,
            new ExtractedConjunct(querySpec.Where!, ConjunctOrigin.Where));
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA122-02, SEC-FED-19
    public void T_SEC_DATA122_01_MaskedColumn_NeverPushed()
    {
        var meta = CreateTable(
            "crm", "customers", DataSourceType.Sql, ["id", "ssn", "email", "salary", "status"],
            masking: new Dictionary<string, MaskingRule>
            {
                ["ssn"] = new MaskingRule { RuleType = "HMAC", HmacKeyId = "k1" }
            });
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["salary"] = ColumnAccessLevel.Deny,
            ["status"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var options = new PredicatePushdownOptions();

        // 1. Column with catalog masking rule (HMAC) must never be pushed
        var sqlMask = "SELECT * FROM customers WHERE ssn = '123-45-6789'";
        var conjunctMask = CreateSingleTableConjunct(sqlMask, meta, "ssn");
        var evalMask = PushabilityEvaluator.Evaluate(conjunctMask, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalMask.IsPushable.ShouldBeFalse();
        evalMask.RejectionReason.ShouldBe(RejectionReason.MaskedColumn);

        // 2. Column with decision Mask must never be pushed
        var sqlHmac = "SELECT * FROM customers WHERE email = 'user@example.com'";
        var conjunctHmac = CreateSingleTableConjunct(sqlHmac, meta, "email");
        var evalHmac = PushabilityEvaluator.Evaluate(conjunctHmac, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalHmac.IsPushable.ShouldBeFalse();
        evalHmac.RejectionReason.ShouldBe(RejectionReason.MaskedColumn);

        // 3. Column with Deny must never be pushed
        var sqlDeny = "SELECT * FROM customers WHERE salary > 50000";
        var conjunctDeny = CreateSingleTableConjunct(sqlDeny, meta, "salary");
        var evalDeny = PushabilityEvaluator.Evaluate(conjunctDeny, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalDeny.IsPushable.ShouldBeFalse();
        evalDeny.RejectionReason.ShouldBe(RejectionReason.MaskedColumn);

        // 4. Column with Clear is allowed
        var sqlClear = "SELECT * FROM customers WHERE status = 'ACTIVE'";
        var conjunctClear = CreateSingleTableConjunct(sqlClear, meta, "status");
        var evalClear = PushabilityEvaluator.Evaluate(conjunctClear, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalClear.IsPushable.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA122-01
    public void T_SEC_DATA122_02_RLS_NarrowingOnly()
    {
        var meta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "tenant_id", "status"]);
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["tenant_id"] = ColumnAccessLevel.Clear,
            ["status"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var args = new Dictionary<string, object?>
        {
            ["tenant_id"] = "tenant-abc"
        };

        var pushdownClause = new TableFilterClause(
            "\"status\" = @p_pd_0",
            new Dictionary<string, object?> { ["@p_pd_0"] = "ACTIVE" },
            ["status"])
        {
            PushdownPredicates = [new PushdownPredicateInfo("status", "Equal")]
        };

        var items = new Dictionary<string, object?>
        {
            [TableQueryItems.Filter] = pushdownClause
        };

        var context = new DataSourceExecutionContext(
            SourceName: "crm",
            Metadata: meta,
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            AccessDecision: decision,
            Arguments: args,
            RequestedFields: ["id", "tenant_id", "status"],
            Limit: 100,
            Offset: 0,
            Tenant: new TenantId("tenant-abc"),
            Items: items);

        var combinedFilter = SqlTableReadExecutor.BuildFilter(context);
        combinedFilter.ShouldNotBeNull();

        // Must strictly narrow with AND, preserving both predicates without widening
        combinedFilter.SqlPredicate.ShouldContain("tenant_id = @p_arg_0");
        combinedFilter.SqlPredicate.ShouldContain("\"status\" = @p_pd_0");
        combinedFilter.SqlPredicate.ShouldContain(" AND ");
        combinedFilter.Parameters["@p_arg_0"].ShouldBe("tenant-abc");
        combinedFilter.Parameters["@p_pd_0"].ShouldBe("ACTIVE");
        combinedFilter.PushdownPredicates.ShouldNotBeNull();
        combinedFilter.PushdownPredicates.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-ADG-02
    public void T_SEC_DATA122_03_ParameterCollision_FailClosed()
    {
        var meta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "code"]);
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["code"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        // Argument filter binds @p_arg_0
        var args = new Dictionary<string, object?> { ["id"] = 1 };

        // Malicious/colliding pushdown clause that also binds @p_arg_0
        var collidingClause = new TableFilterClause(
            "code = @p_arg_0",
            new Dictionary<string, object?> { ["@p_arg_0"] = "malicious_override" },
            ["code"]);

        var items = new Dictionary<string, object?>
        {
            [TableQueryItems.Filter] = collidingClause
        };

        var context = new DataSourceExecutionContext(
            SourceName: "crm",
            Metadata: meta,
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            AccessDecision: decision,
            Arguments: args,
            RequestedFields: ["id", "code"],
            Limit: 100,
            Offset: 0,
            Tenant: new TenantId("t1"),
            Items: items);

        // Duplicate parameter name must fail closed
        var ex = Should.Throw<SecurityException>(() => SqlTableReadExecutor.BuildFilter(context));
        ex.Message.ShouldContain("fail-closed");
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA122-04
    public void T_SEC_DATA122_04_SupersetParity_DialectMatrix()
    {
        var metaSqlServer = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "title"], dialect: DatabaseDialect.SqlServer);
        var decisionSqlServer = TableAccessDecision.Allowed(metaSqlServer.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["title"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var options = new PredicatePushdownOptions();

        // 1. SQL Server string inequality without exact_compare is rejected for CollationRisk
        var sqlIneq = "SELECT * FROM orders WHERE title <> 'abc'";
        var conjIneq = CreateSingleTableConjunct(sqlIneq, metaSqlServer, "title");
        var evalIneq = PushabilityEvaluator.Evaluate(conjIneq, metaSqlServer, decisionSqlServer, DatabaseDialect.SqlServer, null, options);
        evalIneq.IsPushable.ShouldBeFalse();
        evalIneq.RejectionReason.ShouldBe(RejectionReason.CollationRisk);

        // 2. SQL Server string equality IS allowed (superset-safe)
        var sqlEq = "SELECT * FROM orders WHERE title = 'abc'";
        var conjEq = CreateSingleTableConjunct(sqlEq, metaSqlServer, "title");
        var evalEq = PushabilityEvaluator.Evaluate(conjEq, metaSqlServer, decisionSqlServer, DatabaseDialect.SqlServer, null, options);
        evalEq.IsPushable.ShouldBeTrue();

        // 3. SQL Server LIKE bracket pattern is rejected
        var sqlBracket = "SELECT * FROM orders WHERE title LIKE '[A-Z]%'";
        var conjBracket = CreateSingleTableConjunct(sqlBracket, metaSqlServer, "title");
        var evalBracket = PushabilityEvaluator.Evaluate(conjBracket, metaSqlServer, decisionSqlServer, DatabaseDialect.SqlServer, null, options);
        evalBracket.IsPushable.ShouldBeFalse();
        evalBracket.RejectionReason.ShouldBe(RejectionReason.UnsupportedExpression);

        // 4. PostgreSQL LIKE bracket pattern IS allowed
        var metaPg = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "title"], dialect: DatabaseDialect.PostgreSql);
        var decisionPg = TableAccessDecision.Allowed(metaPg.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["title"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);
        var conjBracketPg = CreateSingleTableConjunct(sqlBracket, metaPg, "title");
        var evalBracketPg = PushabilityEvaluator.Evaluate(conjBracketPg, metaPg, decisionPg, DatabaseDialect.PostgreSql, null, options);
        evalBracketPg.IsPushable.ShouldBeTrue();

        // 5. Column with exact_compare hint allows string inequality on SQL Server
        var metaExact = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "title"], dialect: DatabaseDialect.SqlServer,
            columnMeta: new Dictionary<string, string> { ["exact_compare"] = "true" });
        var conjExact = CreateSingleTableConjunct(sqlIneq, metaExact, "title");
        var evalExact = PushabilityEvaluator.Evaluate(conjExact, metaExact, decisionSqlServer, DatabaseDialect.SqlServer, null, options);
        evalExact.IsPushable.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-FED-20
    public void T_SEC_DATA122_05_ThreeValuedLogic_NullInNotIn()
    {
        var meta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "status"]);
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["status"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);
        var options = new PredicatePushdownOptions();

        // 1. NOT IN with NULL -> rejected (3VL hazard)
        var sqlNotInNull = "SELECT * FROM orders WHERE status NOT IN ('X', 'Y', NULL)";
        var conjNotInNull = CreateSingleTableConjunct(sqlNotInNull, meta, "status");
        var evalNotInNull = PushabilityEvaluator.Evaluate(conjNotInNull, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalNotInNull.IsPushable.ShouldBeFalse();
        evalNotInNull.RejectionReason.ShouldBe(RejectionReason.NullInNotInList);

        // 2. IN with NULL -> allowed (superset-safe)
        var sqlInNull = "SELECT * FROM orders WHERE status IN ('X', 'Y', NULL)";
        var conjInNull = CreateSingleTableConjunct(sqlInNull, meta, "status");
        var evalInNull = PushabilityEvaluator.Evaluate(conjInNull, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalInNull.IsPushable.ShouldBeTrue();

        // 3. IS NULL -> allowed
        var sqlIsNull = "SELECT * FROM orders WHERE status IS NULL";
        var conjIsNull = CreateSingleTableConjunct(sqlIsNull, meta, "status");
        var evalIsNull = PushabilityEvaluator.Evaluate(conjIsNull, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalIsNull.IsPushable.ShouldBeTrue();

        // 4. IS NOT NULL -> allowed
        var sqlIsNotNull = "SELECT * FROM orders WHERE status IS NOT NULL";
        var conjIsNotNull = CreateSingleTableConjunct(sqlIsNotNull, meta, "status");
        var evalIsNotNull = PushabilityEvaluator.Evaluate(conjIsNotNull, meta, decision, DatabaseDialect.PostgreSql, null, options);
        evalIsNotNull.IsPushable.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA122-03, SEC-DATA122-06
    public void T_SEC_DATA122_06_MultiTableAndOrIsolation()
    {
        var sql = "SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id WHERE (o.total > 100 AND o.status = 'PAID') AND (o.id = 1 OR c.id = 2)";
        var statement = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "customer_id", "total", "status"]);
        var customerMeta = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "name", "status"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tCustomers = new ResolvedSourceTable(metadata.ReferencedTables[1], customerMeta.Identifier, customerMeta, "crm");

        var conjuncts = SqlConjunctSplitter.Split(statement);
        var classification = SingleTableConjunctClassifier.Classify(conjuncts, [tOrders, tCustomers]);

        // (o.id = 1 OR c.id = 2) and o.customer_id = c.id must be classified as MultiTableConjuncts
        classification.MultiTableConjuncts.Count.ShouldBe(2);

        // Single-table conjuncts only contain orders total and status
        classification.SingleTableConjuncts.Count.ShouldBe(2);
        classification.SingleTableConjuncts.All(c => c.Table == orderMeta.Identifier).ShouldBeTrue();

        // Outer Join Isolation:
        // In LEFT OUTER JOIN, WHERE conjunct on null-supplying right side (customers) must not be pushed to customers
        var leftJoinSql = "SELECT * FROM orders o LEFT JOIN customers c ON o.customer_id = c.id WHERE c.name = 'Alice'";
        var leftJoinStmt = Parse(leftJoinSql);
        var leftJoinConjuncts = SqlConjunctSplitter.Split(leftJoinStmt);
        var leftJoinClass = SingleTableConjunctClassifier.Classify(leftJoinConjuncts, [tOrders, tCustomers]);

        leftJoinClass.SingleTableConjuncts.Any(c => c.Table == customerMeta.Identifier).ShouldBeFalse();

        // Full outer join excludes all pushdown conjuncts completely
        var fojSql = "SELECT * FROM orders o FULL OUTER JOIN customers c ON o.customer_id = c.id WHERE o.total > 50";
        var fojStmt = Parse(fojSql);
        var fojConjuncts = SqlConjunctSplitter.Split(fojStmt);
        fojConjuncts.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA122-05
    public void T_SEC_DATA122_07_HttpSource_Exempt()
    {
        var httpMeta = CreateTable("api", "endpoints", DataSourceType.HttpDeclarative, ["id", "name"]);
        var decision = TableAccessDecision.Allowed(httpMeta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var sql = "SELECT * FROM endpoints WHERE name = 'test'";
        var conj = CreateSingleTableConjunct(sql, httpMeta, "name");

        var eval = PushabilityEvaluator.Evaluate(conj, httpMeta, decision, DatabaseDialect.PostgreSql, null, new PredicatePushdownOptions());
        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.NotSqlSource);
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-FED-23
    public void T_SEC_DATA122_08_VirtualFilter_Exempt()
    {
        var table = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "account_id", "name"]);
        var decision = TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["account_id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var pair = VirtualFilterEnforcementFixture.PairById();
        var keys = VirtualFilterEnforcementFixture.Keys([VirtualFilterKeyType.Text], ["1"]);
        var enforcement = VirtualFilterEnforcementFixture.Enforcement(table, (pair, keys));
        var vfPlan = new VirtualFilterTablePlan(enforcement, VirtualFilterTier.FullFetch, skipUpstream: false, "test");

        var sql = "SELECT * FROM customers WHERE name = 'Alice'";
        var conj = CreateSingleTableConjunct(sql, table, "name");

        var eval = PushabilityEvaluator.Evaluate(conj, table, decision, DatabaseDialect.PostgreSql, vfPlan, new PredicatePushdownOptions());
        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.GatewayEnforcedTable);
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA122-07
    public void T_SEC_DATA122_09_Audit_StructuralZeroPayload()
    {
        var meta = CreateTable("crm", "users", DataSourceType.Sql, ["id", "password_hash", "status"]);
        var sensitiveValue = "SUPER_SECRET_TOKEN_99999";
        var sql = $"SELECT * FROM users WHERE status = '{sensitiveValue}'";
        var stmt = Parse(sql);
        var querySpec = (QuerySpecification)((SelectStatement)stmt).Body;

        var conjuncts = new List<Expression>();
        SqlConjunctSplitter.FlattenAnd(querySpec.Where!, conjuncts);

        var singleConjuncts = conjuncts.Select(c => new SingleTableConjunct(
            meta.Identifier,
            c,
            ["status"],
            new ExtractedConjunct(c, ConjunctOrigin.Where))).ToList();

        var clause = PushedClauseBuilder.Build(singleConjuncts, meta);

        clause.PushdownPredicates.ShouldNotBeNull();
        clause.PushdownPredicates.Count.ShouldBe(1);

        var pred = clause.PushdownPredicates[0];
        pred.Column.ShouldBe("status");
        pred.Operator.ShouldBe("Equal");

        var properties = typeof(PushdownPredicateInfo).GetProperties();
        properties.Select(p => p.Name).OrderBy(n => n).ShouldBe(["Column", "Operator"]);

        pred.ToString().ShouldNotContain(sensitiveValue);

        var analyzed = new AnalyzedPushedPredicate("status", "Equal");
        var analyzedProps = typeof(AnalyzedPushedPredicate).GetProperties();
        analyzedProps.Select(p => p.Name).OrderBy(n => n).ShouldBe(["Column", "Operator"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA122_10_RowLimitRelief()
    {
        var meta = CreateTable("crm", "events", DataSourceType.Sql, ["id", "event_type", "created_at"]);
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["event_type"] = ColumnAccessLevel.Clear,
            ["created_at"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var pushdownClause = new TableFilterClause(
            "\"event_type\" = @p_pd_0",
            new Dictionary<string, object?> { ["@p_pd_0"] = "LOGIN_FAILURE" },
            ["event_type"])
        {
            TrinoSqlFactory = resolve => $"\"{resolve("event_type")}\" = @p_pd_0",
            PushdownPredicates = [new PushdownPredicateInfo("event_type", "Equal")]
        };

        var items = new Dictionary<string, object?>
        {
            [TableQueryItems.Filter] = pushdownClause
        };

        var context = new DataSourceExecutionContext(
            SourceName: "crm",
            Metadata: meta,
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            AccessDecision: decision,
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "event_type"],
            Limit: 50,
            Offset: 0,
            Tenant: new TenantId("tenant-1"),
            Items: items);

        var combinedFilter = SqlTableReadExecutor.BuildFilter(context);
        combinedFilter.ShouldNotBeNull();
        combinedFilter.SqlPredicate.ShouldBe("\"event_type\" = @p_pd_0");
        combinedFilter.Parameters["@p_pd_0"].ShouldBe("LOGIN_FAILURE");

        var planner = new GovernedTableReadPlanner();
        var intent = new TableReadIntent(
            meta.Identifier,
            ["id", "event_type"],
            combinedFilter,
            null,
            50,
            0,
            SqlTransports.Rest);

        var planned = planner.Plan(intent, meta, decision);

        planned.Sql.ShouldContain("WHERE \"event_type\" = @p_pd_0");
        planned.Sql.ShouldContain("LIMIT 50");
        planned.Parameters["@p_pd_0"].ShouldBe("LOGIN_FAILURE");
    }
}
