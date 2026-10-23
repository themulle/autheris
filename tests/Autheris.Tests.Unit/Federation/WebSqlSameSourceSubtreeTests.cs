namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class WebSqlSameSourceSubtreeTests
{
    private static readonly FastSqlEngine Engine = new();

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

    [Fact]
    public void Partition_ThreePostgresAndTwoSqlServer_PartitionsIntoTwoSubtrees()
    {
        var sql = "SELECT o.id, oi.item_name, c.name, s.tracking, ca.carrier_name " +
                  "FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN pg.public.customers c ON o.cust_id = c.id " +
                  "JOIN ms.dbo.shipments s ON o.shipment_id = s.id " +
                  "JOIN ms.dbo.carriers ca ON s.carrier_id = ca.id";

        var stmt = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id", "order_id", "cust_id", "shipment_id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var custMeta = CreateTable("pg", "public", "customers", DataSourceType.Sql, ["id", "name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id", "carrier_id", "tracking"], DatabaseDialect.SqlServer);
        var carrierMeta = CreateTable("ms", "dbo", "carriers", DataSourceType.Sql, ["id", "carrier_name"], DatabaseDialect.SqlServer);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tCust = new ResolvedSourceTable(metadata.ReferencedTables[2], custMeta.Identifier, custMeta, "pg");
        var tShip = new ResolvedSourceTable(metadata.ReferencedTables[3], shipMeta.Identifier, shipMeta, "ms");
        var tCarrier = new ResolvedSourceTable(metadata.ReferencedTables[4], carrierMeta.Identifier, carrierMeta, "ms");

        var tables = new List<ResolvedSourceTable> { tOrders, tItems, tCust, tShip, tCarrier };

        // Test AST Partitioner directly
        var partitionResult = SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 12);

        partitionResult.Subtrees.Count.ShouldBe(2);
        partitionResult.RemainingSingleTables.Count.ShouldBe(0);

        var pgSubtree = partitionResult.Subtrees.Single(s => s.SourceName == "pg");
        pgSubtree.MemberTables.Count.ShouldBe(3);
        pgSubtree.Dialect.ShouldBe(TargetSqlDialect.PostgreSql);

        var msSubtree = partitionResult.Subtrees.Single(s => s.SourceName == "ms");
        msSubtree.MemberTables.Count.ShouldBe(2);
        msSubtree.Dialect.ShouldBe(TargetSqlDialect.SqlServer);

        // Test CrossSourcePlanner
        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true, MaxTablesPerSubtree = 12 }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        plan.StagingRequests.Count.ShouldBe(2);
        plan.StagingRequests[0].Subtree.ShouldNotBeNull();
        plan.StagingRequests[1].Subtree.ShouldNotBeNull();

        plan.GeneratedDuckDbSql.ShouldContain("\"s0\"");
        plan.GeneratedDuckDbSql.ShouldContain("\"s1\"");
        plan.GeneratedDuckDbSql.ShouldNotContain("pg.public");
        plan.GeneratedDuckDbSql.ShouldNotContain("ms.dbo");
    }

    [Fact]
    public void Partition_MixedPostgresAndHttp_ExtractsPostgresSubtreeAndLeavesHttpSingle()
    {
        var sql = "SELECT o.id, oi.item_name, h.status " +
                  "FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN http.api.tracking h ON oi.tracking_id = h.id";

        var stmt = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id", "shipment_id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name", "tracking_id"], DatabaseDialect.PostgreSql);
        var trackMeta = CreateTable("http", "api", "tracking", DataSourceType.HttpDeclarative, ["id", "status"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tTrack = new ResolvedSourceTable(metadata.ReferencedTables[2], trackMeta.Identifier, trackMeta, "http");

        var tables = new List<ResolvedSourceTable> { tOrders, tItems, tTrack };

        var partitionResult = SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 12);

        partitionResult.Subtrees.Count.ShouldBe(1);
        partitionResult.Subtrees[0].SourceName.ShouldBe("pg");
        partitionResult.Subtrees[0].MemberTables.Count.ShouldBe(2);

        partitionResult.RemainingSingleTables.Count.ShouldBe(1);
        partitionResult.RemainingSingleTables[0].Target.TableName.ShouldBe("tracking");

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        plan.StagingRequests.Count.ShouldBe(2);
        plan.StagingRequests[0].Subtree.ShouldNotBeNull();
        plan.StagingRequests[0].Subtree!.SourceName.ShouldBe("pg");
        plan.StagingRequests[1].Subtree.ShouldBeNull();
        plan.StagingRequests[1].Metadata.Identifier.ShouldBe(trackMeta.Identifier);
    }

    [Fact]
    public void Partition_OuterJoinBoundary_PreservesBoundaryAndSubtree()
    {
        var sql = "SELECT o.id, oi.item_name, h.status " +
                  "FROM pg.public.orders o " +
                  "LEFT JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "LEFT JOIN http.api.tracking h ON oi.tracking_id = h.id";

        var stmt = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name", "tracking_id"], DatabaseDialect.PostgreSql);
        var trackMeta = CreateTable("http", "api", "tracking", DataSourceType.HttpDeclarative, ["id", "status"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tTrack = new ResolvedSourceTable(metadata.ReferencedTables[2], trackMeta.Identifier, trackMeta, "http");

        var tables = new List<ResolvedSourceTable> { tOrders, tItems, tTrack };

        var partitionResult = SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 12);

        partitionResult.Subtrees.Count.ShouldBe(1);
        var subtree = partitionResult.Subtrees[0];
        subtree.MemberTables.Count.ShouldBe(2);
        subtree.RootTableSource.ShouldBeOfType<JoinedTableSource>();
        ((JoinedTableSource)subtree.RootTableSource).Type.ShouldBe(JoinType.LeftOuter);

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        plan.GeneratedDuckDbSql.ShouldContain("LEFT OUTER JOIN \"s1\"");
    }

    [Fact]
    public void Subtree_AmbiguousColumnProjection_DisambiguatesAliases()
    {
        var sql = "SELECT o.id, oi.id, oi.item_name " +
                  "FROM pg.public.orders o " +
                  "JOIN pg.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN ms.dbo.shipments s ON o.id = s.order_id";

        var stmt = Parse(sql);
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("pg", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemMeta = CreateTable("pg", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name"], DatabaseDialect.PostgreSql);
        var shipMeta = CreateTable("ms", "dbo", "shipments", DataSourceType.Sql, ["id", "order_id"], DatabaseDialect.SqlServer);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg");
        var tItems = new ResolvedSourceTable(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg");
        var tShip = new ResolvedSourceTable(metadata.ReferencedTables[2], shipMeta.Identifier, shipMeta, "ms");

        var tables = new List<ResolvedSourceTable> { tOrders, tItems, tShip };

        var partitionResult = SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 12);
        var pgSubtree = partitionResult.Subtrees.Single(s => s.SourceName == "pg");

        // Both orders and order_items have column 'id' referenced. Disambiguation aliases: o_id, oi_id
        pgSubtree.ProjectedItems.Count.ShouldBeGreaterThanOrEqualTo(2);
        var selectItemAliases = pgSubtree.ProjectedItems.OfType<ColumnSelectItem>().Select(ci => ci.Alias?.Value).ToList();
        selectItemAliases.ShouldContain("o_id");
        selectItemAliases.ShouldContain("oi_id");

        var decisions = tables.ToDictionary(
            t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        plan.StagingRequests[0].Subtree.ShouldNotBeNull();
        plan.StagingRequests[0].Subtree!.ProjectedColumns.ShouldContain("o_id");
        plan.StagingRequests[0].Subtree!.ProjectedColumns.ShouldContain("oi_id");
    }

    [Fact]
    public async Task QueryAnalysis_CrossSourceWithSubtrees_ReportsAnalyzedSubtrees()
    {
        var sql = "SELECT o.id, oi.item_name, s.status " +
                  "FROM sales.public.orders o " +
                  "JOIN sales.public.order_items oi ON o.id = oi.order_id " +
                  "JOIN inventory.dbo.stocks s ON oi.item_id = s.id";

        var ordersTable = CreateTable("sales", "public", "orders", DataSourceType.Sql, ["id"], DatabaseDialect.PostgreSql);
        var itemsTable = CreateTable("sales", "public", "order_items", DataSourceType.Sql, ["id", "order_id", "item_name", "item_id"], DatabaseDialect.PostgreSql);
        var stocksTable = CreateTable("inventory", "dbo", "stocks", DataSourceType.Sql, ["id", "status"], DatabaseDialect.SqlServer);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ordersTable, itemsTable, stocksTable });

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Consent { TableIdentifier = ordersTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(10) },
                new Consent { TableIdentifier = itemsTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(10) },
                new Consent { TableIdentifier = stocksTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(10) }
            });

        var analysisService = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            analysisOptions: new QueryAnalysisOptions());

        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("sid", "user-1")], "TestAuth"));

        var result = await analysisService.AnalyzeQueriesAsync(
            new[] { QueryAnalysisItem.FromSql(sql, name: "cross_source_subtree_query") },
            caller);

        result.ValidationReport.IsValid.ShouldBeTrue();
        result.Federation.ShouldNotBeNull();
        result.Federation.Subtrees.ShouldNotBeNull();
        result.Federation.Subtrees.Count.ShouldBe(1);

        var subtree = result.Federation.Subtrees[0];
        subtree.SourceName.ShouldBe("sales");
        subtree.MemberTables.Count.ShouldBe(2);
        subtree.MemberTables.ShouldContain(ordersTable.Identifier.ToString());
        subtree.MemberTables.ShouldContain(itemsTable.Identifier.ToString());
    }
}
