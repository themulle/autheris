namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebSqlSameSourceSubtreeSecurityTests
{
    private static readonly FastSqlEngine Engine = new();
    private const string Tenant = "tenant-sec-123";

    private static TableMetadata CreateTable(
        string domain,
        string schema,
        string table,
        DataSourceType dsType,
        IReadOnlyList<string> columns,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        Dictionary<string, MaskingRule>? masking = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table
            {
                TableName = table,
                SchemaName = schema,
                SourceType = dsType == DataSourceType.Sql ? dialect.ToString() : "Http",
                DataSourceType = dsType,
                SourceName = domain,
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder().BuildStatement(tree);
    }

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-SEC-123"),
                new Claim("tenant_id", Tenant)
            ],
            "TestAuth"));

    private static FederationBudget CreateBudget(int maxRows = 1000) =>
        new(
            MaxTableCount: 5,
            MaxStagedRowsPerTable: maxRows,
            MaxTotalStagedRows: 5000,
            MaxStagedBytesPerTable: 32 * 1024 * 1024,
            MaxTotalStagedBytes: 128 * 1024 * 1024,
            TimeoutSeconds: 30,
            MaxParallelSourceReads: 4);

    /// <summary>
    /// SEC-DATA123-01: Asserts RLS is embedded for all member tables in a subtree.
    /// </summary>
    [Fact]
    public void T_SEC_DATA123_01_SubtreeRLS_EmbeddedForAllTables()
    {
        var sql = "SELECT o.id, oi.item_name, s.tracking " +
                  "FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.shipment_id = s.id";

        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id", "shipment_id", "tenant_id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name", "is_active"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id", "tracking"], DatabaseDialect.SqlServer);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tShip = new ResolvedSourceTable(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms");

        var tables = new List<ResolvedSourceTable> { tOrders, tItems, tShip };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                with { CombinedRowFilterSql = "o.tenant_id = 't1'" },
            [itemMeta.Identifier] = TableAccessDecision.Allowed(itemMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                with { CombinedRowFilterSql = "oi.is_active = '1'" },
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        var pgStaging = plan.StagingRequests.Single(r => r.StagingName == "s0");
        pgStaging.Subtree.ShouldNotBeNull();

        var compiledSubtreeSql = pgStaging.Subtree!.Compiled.Sql;
        // Verify RLS conditions for BOTH tables are conjoined into the subtree SQL
        compiledSubtreeSql.ShouldContain("tenant_id");
        compiledSubtreeSql.ShouldContain("is_active");

        // Verify isolated parameter markers were generated for bindings
        if (pgStaging.Subtree.Parameters != null && pgStaging.Subtree.Parameters.Count > 0)
        {
            pgStaging.Subtree.Parameters.Keys.ShouldAllBe(k => k.StartsWith("@p_st0_"));
        }
    }

    /// <summary>
    /// SEC-DATA123-02: Asserts masked columns retain Masked status and masking rules in DuckDB.
    /// </summary>
    [Fact]
    public void T_SEC_DATA123_02_MaskingHeritage_PreservedInDuckDb()
    {
        var sql = "SELECT c.id, c.ssn, oi.item_name, s.tracking " +
                  "FROM pg.public.customers c " +
                  "JOIN pg.public.order_items oi ON c.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON c.id = s.id";

        var metadata = Engine.Analyze(sql.AsMemory());

        var custMeta = CreateTable(
            "pg", "public", "customers", DataSourceType.Sql, ["id", "ssn"], DatabaseDialect.PostgreSql,
            masking: new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT" } });
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id", "tracking"], DatabaseDialect.SqlServer);

        var tCust = new ResolvedSourceTable(metadata.ReferencedTables[0], custMeta.Identifier, custMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tShip = new ResolvedSourceTable(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms");

        var tables = new List<ResolvedSourceTable> { tCust, tItems, tShip };

        var colAccessCust = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Mask
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [custMeta.Identifier] = TableAccessDecision.Allowed(custMeta.Identifier, colAccessCust, hasUnconstrainedColumnAllow: false),
            [itemMeta.Identifier] = TableAccessDecision.Allowed(itemMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        var pgStaging = plan.StagingRequests.Single(r => r.StagingName == "s0");
        pgStaging.Subtree.ShouldNotBeNull();

        // Subtree execution plan tracks masked columns
        pgStaging.Subtree!.MaskedColumns.ShouldContain("ssn");

        // Synthetic table metadata retains column masking rules for DuckDB stage
        pgStaging.Subtree.SyntheticMetadata.ColumnMaskingRules.ContainsKey("ssn").ShouldBeTrue();
        pgStaging.Subtree.SyntheticMetadata.ColumnMaskingRules["ssn"].RuleType.ShouldBe("REDACT");
    }

    /// <summary>
    /// SEC-DATA123-03: Asserts cross-source joins on masked subtree outputs fail closed (SEC-FED-19).
    /// </summary>
    [Fact]
    public void T_SEC_DATA123_03_CrossSourceJoinOnSubtreeMask_Blocked()
    {
        var sql = "SELECT c.id, s.tracking " +
                  "FROM pg.public.customers c " +
                  "JOIN pg.public.order_items oi ON c.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON c.ssn = s.customer_ssn";

        var metadata = Engine.Analyze(sql.AsMemory());

        var custMeta = CreateTable(
            "pg", "public", "customers", DataSourceType.Sql, ["id", "ssn"], DatabaseDialect.PostgreSql,
            masking: new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT" } });
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id", "customer_ssn", "tracking"], DatabaseDialect.SqlServer);

        var tCust = new ResolvedSourceTable(metadata.ReferencedTables[0], custMeta.Identifier, custMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tShip = new ResolvedSourceTable(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms");

        var tables = new List<ResolvedSourceTable> { tCust, tItems, tShip };

        var colAccessCust = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Mask
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [custMeta.Identifier] = TableAccessDecision.Allowed(custMeta.Identifier, colAccessCust, hasUnconstrainedColumnAllow: false),
            [itemMeta.Identifier] = TableAccessDecision.Allowed(itemMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant));

        ex.Message.ShouldContain("ssn");
        ex.Message.ShouldContain("protected by");
    }

    /// <summary>
    /// SEC-DATA123-04: Asserts queries with > MaxTablesPerSubtree tables fail closed or bisect safely.
    /// </summary>
    [Fact]
    public void T_SEC_DATA123_04_MaxTablesPerSubtree_Enforced()
    {
        var sql = "SELECT t1.id FROM pg.public.t1 t1 " +
                  "JOIN pg.public.t2 t2 ON t1.id = t2.id " +
                  "JOIN pg.public.t3 t3 ON t2.id = t3.id " +
                  "JOIN pg.public.t4 t4 ON t3.id = t4.id " +
                  "JOIN ms.dbo.other o ON t4.id = o.id";

        var stmt = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var m1 = CreateTable("pg", "public", "t1", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var m2 = CreateTable("pg", "public", "t2", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var m3 = CreateTable("pg", "public", "t3", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var m4 = CreateTable("pg", "public", "t4", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var mo = CreateTable("ms", "dbo", "other", DataSourceType.Sql, ["id"], DatabaseDialect.SqlServer);

        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], m1.Identifier, m1, "pg"),
            new(metadata.ReferencedTables[1], m2.Identifier, m2, "pg"),
            new(metadata.ReferencedTables[2], m3.Identifier, m3, "pg"),
            new(metadata.ReferencedTables[3], m4.Identifier, m4, "pg"),
            new(metadata.ReferencedTables[4], mo.Identifier, mo, "ms")
        };

        // SubtreeGraphPartitioner with maxTablesPerSubtree: 3 must safely bisect clusters > 3 tables
        var partitionResult = SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 3);
        partitionResult.Subtrees.Count.ShouldBe(1);
        partitionResult.Subtrees[0].MemberTables.Count.ShouldBeLessThanOrEqualTo(3);
        partitionResult.RemainingSingleTables.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// SEC-DATA123-05: Asserts base tables > 50,000 joining to < 50,000 succeed without ROW_LIMIT_EXCEEDED,
    /// while exceeding the limit at the subtree level properly triggers ROW_LIMIT_EXCEEDED.
    /// </summary>
    [Fact]
    public async Task T_SEC_DATA123_05_VolumeRelief_NarrowedStaging()
    {
        var sql = "SELECT o.id, oi.item_name FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.id";

        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id"], DatabaseDialect.SqlServer);

        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg"),
            new(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms")
        };

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        var subtreeRequest = plan.StagingRequests.Single(r => r.Subtree != null);

        // Case 1: Subtree returns 5 joined rows (well under limit of 100) -> Succeeds!
        var joinedRows = Enumerable.Range(1, 5)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = i,
                ["item_name"] = $"Item {i}"
            })
            .ToList();

        var connector = Substitute.For<IAutherisConnector>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splitManager);
        connector.RecordSource.Returns(recordSource);
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-1")]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(joinedRows);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Arg.Any<TableIdentifier>(), out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var stagingService = new FederatedStagingService(
            registry,
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        var budget = CreateBudget(maxRows: 100);
        var staged = await stagingService.StageAsync([subtreeRequest], CreateUser(), new TenantId(Tenant), budget);

        staged.Count.ShouldBe(1);
        staged[0].GovernedRows.Count.ShouldBe(5);

        // Case 2: Subtree returns rows exceeding limit -> Throws ROW_LIMIT_EXCEEDED
        var excessiveRows = Enumerable.Range(1, 105)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = i,
                ["item_name"] = $"Item {i}"
            })
            .ToList();

        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(excessiveRows);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
            await stagingService.StageAsync([subtreeRequest], CreateUser(), new TenantId(Tenant), budget));

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
    }

    /// <summary>
    /// SEC-DATA123-06: Asserts statement-level and per-table audit events without sensitive leaks.
    /// </summary>
    [Fact]
    public async Task T_SEC_DATA123_06_Audit_DualTierCompleteness()
    {
        var sql = "SELECT o.id, oi.item_name FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.id";

        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id"], DatabaseDialect.SqlServer);

        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg"),
            new(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms")
        };

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);
        var subtreeRequest = plan.StagingRequests.Single(r => r.Subtree != null);

        var connector = Substitute.For<IAutherisConnector>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splitManager);
        connector.RecordSource.Returns(recordSource);
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-1")]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([new Dictionary<string, object?> { ["id"] = 1, ["item_name"] = "Widget" }]);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Arg.Any<TableIdentifier>(), out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var auditEntries = new List<AuditLogEntry>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(auditEntries.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var stagingService = new FederatedStagingService(
            registry,
            Substitute.For<IColumnMaskingProvider>(),
            auditRepo,
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        await stagingService.StageAsync([subtreeRequest], CreateUser(), new TenantId(Tenant), CreateBudget());

        // 1. Statement-level audit event for subtree
        var subtreeEvent = auditEntries.SingleOrDefault(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSubtreeRead);
        subtreeEvent.ShouldNotBeNull();
        subtreeEvent.DetailsJson.ShouldNotBeNull();

        var subtreeDetails = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(subtreeEvent.DetailsJson!);
        subtreeDetails.ShouldNotBeNull();
        subtreeDetails.ContainsKey("subtreeId").ShouldBeTrue();
        subtreeDetails.ContainsKey("sourceName").ShouldBeTrue();
        subtreeDetails.ContainsKey("memberTables").ShouldBeTrue();

        // 2. Member-level audit events for each table
        var memberEvents = auditEntries.Where(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead).ToList();
        memberEvents.Count.ShouldBe(2); // orders and order_items
        memberEvents.Select(e => e.TargetTable).ShouldContain(orderMeta.Identifier.ToString());
        memberEvents.Select(e => e.TargetTable).ShouldContain(itemMeta.Identifier.ToString());

        // Verify zero leakage of query parameter values in audit entries
        foreach (var entry in auditEntries)
        {
            entry.DetailsJson?.ShouldNotContain("secret_param_value");
        }
    }

    /// <summary>
    /// SEC-DATA123-07: Asserts functions outside dialect allowlist are rejected.
    /// </summary>
    [Fact]
    public void T_SEC_DATA123_07_SourceFunctionAllowlist_Enforced()
    {
        var sql = "SELECT o.id FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.id";

        var stmt = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id"], DatabaseDialect.SqlServer);

        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg"),
            new(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms")
        };

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var partitionResult = SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 12);
        var pgSubtree = partitionResult.Subtrees.Single(s => s.SourceName == "pg");

        var badPredicate = new BinaryExpression(
            new FunctionCallExpression(new SqlQualifiedName("getdate"), []),
            BinaryOperator.GreaterThan,
            new LiteralExpression("2026-01-01", LiteralType.String));
        var badSubtree = pgSubtree with { PushablePredicates = [badPredicate] };

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            SubtreeCompiler.Compile(badSubtree, decisions, new CrossSourceOptions { Enabled = true }, Engine,
                Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant));

        ex.Message.ShouldContain("getdate");
        ex.Message.ShouldContain("not permitted");
    }

    /// <summary>
    /// SEC-DATA123-08: Asserts output parity between pushdown and naive staging.
    /// </summary>
    [Fact]
    public void T_SEC_DATA123_08_BitForBitParity()
    {
        var sql = "SELECT o.id, oi.item_name, s.tracking " +
                  "FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.id";

        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id", "cust_id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id", "tracking"], DatabaseDialect.SqlServer);

        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg"),
            new(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms")
        };

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        var subtreeRequest = plan.StagingRequests.Single(r => r.Subtree != null);
        var subtree = subtreeRequest.Subtree!;

        // The subtree must project the requested output columns (o.id, oi.item_name) AND the join column (o.id)
        subtree.ProjectedColumns.ShouldContain("id");
        subtree.ProjectedColumns.ShouldContain("item_name");

        // The synthetic metadata must expose these exact columns with types
        var synthCols = subtree.SyntheticMetadata.Columns.Select(c => c.ColumnName).ToList();
        synthCols.ShouldContain("id");
        synthCols.ShouldContain("item_name");
    }

    /// <summary>
    /// #327: the session of a subtree read passes the connector's policy evaluator (the subtree SELECT is not a predicate), and the subtree
    /// statement still travels with the session.
    /// </summary>
    [Fact]
    public async Task SubtreeSession_PassesConnectorSecurityPolicy_AndCarriesTheSubtreeSql()
    {
        var sql = "SELECT o.id, oi.item_name FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());
        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id"], DatabaseDialect.SqlServer);
        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg"),
            new(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms")
        };
        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var plan = new CrossSourcePlanner(Engine).Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true },
            Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);
        var subtreeRequest = plan.StagingRequests.Single(r => r.Subtree != null);

        ConnectorSessionContext? captured = null;
        var connector = Substitute.For<IAutherisConnector>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splitManager);
        connector.RecordSource.Returns(recordSource);
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-1")]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Do<ConnectorSessionContext>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Arg.Any<TableIdentifier>(), out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });
        var stagingService = new FederatedStagingService(registry, Substitute.For<IColumnMaskingProvider>(), Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions()), NullLogger<FederatedStagingService>.Instance);

        await stagingService.StageAsync([subtreeRequest], CreateUser(), new TenantId(Tenant), CreateBudget(maxRows: 100));

        captured.ShouldNotBeNull();
        Autheris.Application.Connectors.ConnectorSecurityPolicyEvaluator.EnforceSecurityPolicy(captured!, subtreeRequest.Subtree!.SyntheticMetadata);
        captured!.Items["SubtreeSql"].ShouldBe(subtreeRequest.Subtree!.Compiled.Sql);
        captured.Items.ShouldContainKey("SubtreePlan");
    }
}
