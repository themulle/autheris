namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Services;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedAggregationAndMetricsTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");

    private static TableMetadata CreateOrdersMetadata(int? minGroupSize = null, SmallGroupStrategy? strategy = null)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = OrdersTableId,
            MinGroupSize = minGroupSize,
            SmallGroupStrategy = strategy,
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = "default",
                SchemaName = "public",
                TableName = "orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = minGroupSize,
                SmallGroupStrategy = strategy
            },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = tableId, ColumnName = "region", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "category", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "department", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "status", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "amount", DataType = "decimal" },
                new TableColumn { TableId = tableId, ColumnName = "discount_secret", DataType = "decimal", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["discount_secret"] = new MaskingRule { RuleType = "REDACT" }
            }
        };
    }

    private static ClaimsPrincipal CreatePrincipal(string sid, string tenant = "tenant-a", string role = "Analyst") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.Name, sid),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));

    private sealed class InMemoryRowExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default) =>
            Task.FromResult(rows);
    }

    private sealed class CapturingAuditLogRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_EnforcesPerPrincipalRlsFilters_AndComputesAccurateAggregates()
    {
        // Arrange: #28 - Two principals with different RLS row filters execute the same grouped sum/count/avg/min/max query
        var metadata = CreateOrdersMetadata(minGroupSize: 1);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU", ["category"] = "Software", ["amount"] = 100m, ["discount_secret"] = 10m },
            new Dictionary<string, object?> { ["id"] = 2, ["region"] = "EU", ["category"] = "Software", ["amount"] = 300m, ["discount_secret"] = 30m },
            new Dictionary<string, object?> { ["id"] = 3, ["region"] = "EU", ["category"] = "Hardware", ["amount"] = 50m,  ["discount_secret"] = 5m },
            new Dictionary<string, object?> { ["id"] = 4, ["region"] = "US", ["category"] = "Software", ["amount"] = 500m, ["discount_secret"] = 50m },
            new Dictionary<string, object?> { ["id"] = 5, ["region"] = "US", ["category"] = "Software", ["amount"] = 700m, ["discount_secret"] = 70m }
        };

        var euPrincipal = CreatePrincipal("S-1-5-21-EU-USER");
        var usPrincipal = CreatePrincipal("S-1-5-21-US-USER");

        var resolver = Substitute.For<ITableAccessResolver>();
        var clearCols = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["region"] = ColumnAccessLevel.Clear,
            ["category"] = ColumnAccessLevel.Clear,
            ["amount"] = ColumnAccessLevel.Clear,
            ["discount_secret"] = ColumnAccessLevel.Mask
        };

        resolver.ResolveTableAccessAsync(euPrincipal, OrdersTableId, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                metadata,
                TableAccessDecision.Allowed(OrdersTableId, clearCols, rowFilterSql: "region = 'EU'"),
                new TenantId("tenant-a"),
                new Sid("S-1-5-21-EU-USER"),
                euPrincipal));

        resolver.ResolveTableAccessAsync(usPrincipal, OrdersTableId, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                metadata,
                TableAccessDecision.Allowed(OrdersTableId, clearCols, rowFilterSql: "region = 'US'"),
                new TenantId("tenant-a"),
                new Sid("S-1-5-21-US-USER"),
                usPrincipal));

        var auditRepo = new CapturingAuditLogRepository();
        var service = new GovernedAggregationService(
            metadataRepository: metaRepo,
            accessResolver: resolver,
            dataSourceExecutors: [new InMemoryRowExecutor(rows)],
            auditLogRepository: auditRepo,
            analyticsOptions: Options.Create(new AnalyticsOptions { MinimumGroupSize = 1 }));

        var query = new AggregationQuery(
            Table: OrdersTableId,
            GroupBy: ["category"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "row_count"),
                new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount"),
                new AggregateSelection(AggregateFunction.Min, "amount", "min_amount"),
                new AggregateSelection(AggregateFunction.Max, "amount", "max_amount")
            ]);

        // Act
        var euResult = await service.ExecuteAggregationAsync(euPrincipal, query);
        var usResult = await service.ExecuteAggregationAsync(usPrincipal, query);

        // Assert EU results
        euResult.Groups.Count.ShouldBe(2);
        var euSoftware = euResult.Groups.Single(g => (string?)g.GroupKey["category"] == "Software");
        euSoftware.Count.ShouldBe(2);
        euSoftware.Sum["amount"].ShouldBe(400m);
        euSoftware.Avg["amount"].ShouldBe(200m);
        euSoftware.Min["amount"].ShouldBe(100m);
        euSoftware.Max["amount"].ShouldBe(300m);

        euResult.Totals.ShouldNotBeNull();
        euResult.Totals!.Count.ShouldBe(3);
        euResult.Totals.Sum["amount"].ShouldBe(450m);

        // Assert US results
        usResult.Groups.Count.ShouldBe(1);
        var usSoftware = usResult.Groups.Single();
        usSoftware.GroupKey["category"].ShouldBe("Software");
        usSoftware.Count.ShouldBe(2);
        usSoftware.Sum["amount"].ShouldBe(1200m);
        usSoftware.Avg["amount"].ShouldBe(600m);
        usSoftware.Min["amount"].ShouldBe(500m);
        usSoftware.Max["amount"].ShouldBe(700m);

        usResult.Totals.ShouldNotBeNull();
        usResult.Totals!.Count.ShouldBe(2);
        usResult.Totals.Sum["amount"].ShouldBe(1200m);

        auditRepo.Entries.Count.ShouldBe(2);
        auditRepo.Entries.ShouldAllBe(e => e.EventType == AuditEventTypes.AggregateQuery && e.Decision == "ALLOW");
    }

    [Theory]
    [InlineData("groupBy")]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("where")]
    public async Task ExecuteAggregationAsync_OnMaskedColumn_ThrowsMaskedAggregationException(string clauseKind)
    {
        // Arrange: #28 - Grouping by, filtering on, or aggregating a masked column fails closed with AGGREGATION_ON_MASKED_COLUMN
        var metadata = CreateOrdersMetadata(minGroupSize: 1);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var principal = CreatePrincipal("S-1-5-21-ANALYST");
        var decision = TableAccessDecision.Allowed(
            OrdersTableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["category"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear,
                ["discount_secret"] = ColumnAccessLevel.Mask
            });

        var service = new GovernedAggregationService(
            metadataRepository: metaRepo,
            dataSourceExecutors: [new InMemoryRowExecutor([])]);

        AggregationQuery query = clauseKind switch
        {
            "groupBy" => new AggregationQuery(
                OrdersTableId,
                GroupBy: ["discount_secret"],
                Aggregates: [new AggregateSelection(AggregateFunction.Count)],
                PreEvaluatedDecision: decision),
            "where" => new AggregationQuery(
                OrdersTableId,
                GroupBy: ["category"],
                Aggregates: [new AggregateSelection(AggregateFunction.Count)],
                Where: TableFilterClause.FromTrinoText("discount_secret > 10", ["discount_secret"]),
                PreEvaluatedDecision: decision),
            _ => new AggregationQuery(
                OrdersTableId,
                GroupBy: ["category"],
                Aggregates:
                [
                    new AggregateSelection(
                        Enum.Parse<AggregateFunction>(clauseKind, ignoreCase: true),
                        "discount_secret")
                ],
                PreEvaluatedDecision: decision)
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<MaskedAggregationException>(() => service.ExecuteAggregationAsync(principal, query));
        ex.ErrorCode.ShouldBe(MaskedAggregationException.ErrorCodeConst);
        ex.ErrorCode.ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
        ex.ColumnName.ShouldBe("discount_secret");
    }

    [Theory]
    [InlineData("groupBy")]
    [InlineData("where")]
    public async Task ExecuteAggregationAsync_RelationPathInFreeText_IsRejectedBeforeAnySql(string clauseKind)
    {
        // BUG #75 (SEC-B75-07): groupBy/where of the aggregate field are free text; a relation path such as partner.name
        // (an omitted cross-source relation) is no column of the table and never reaches the data source.
        var metadata = CreateOrdersMetadata(minGroupSize: 1);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var principal = CreatePrincipal("S-1-5-21-ANALYST");
        var decision = TableAccessDecision.Allowed(
            OrdersTableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["category"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            });
        var executor = new RecordingRowExecutor();
        var service = new GovernedAggregationService(metadataRepository: metaRepo, dataSourceExecutors: [executor]);

        var query = clauseKind == "groupBy"
            ? new AggregationQuery(
                OrdersTableId,
                GroupBy: ["partner.name"],
                Aggregates: [new AggregateSelection(AggregateFunction.Count)],
                PreEvaluatedDecision: decision)
            : new AggregationQuery(
                OrdersTableId,
                GroupBy: ["category"],
                Aggregates: [new AggregateSelection(AggregateFunction.Count)],
                Where: TableFilterClause.FromTrinoText("partner.name = 'x'", Array.Empty<string>()),
                PreEvaluatedDecision: decision);

        // WP-0 (F-4): an unknown column and a denied column are one indistinguishable error.
        await Should.ThrowAsync<AggregationColumnUnavailableException>(() => service.ExecuteAggregationAsync(principal, query));

        executor.Calls.ShouldBe(0);
    }

    private sealed class RecordingRowExecutor : IDataSourceExecutor
    {
        public int Calls { get; private set; }

        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        }
    }

    [Fact]
    public async Task ExecuteAggregationAsync_EnforcesMinGroupSizeSuppression_AndDifferencingSafeTotals()
    {
        // Arrange: #37 - MinGroupSize = 5 suppresses groups < 5, suppresses filtered groups narrowed to 3 rows,
        // and computes Totals strictly over surviving visible groups.
        var metadata = CreateOrdersMetadata(minGroupSize: 5, strategy: SmallGroupStrategy.Suppress);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        // Group "Engineering": 6 rows (5 active, 1 inactive), each amount = 100
        for (int i = 0; i < 6; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = i + 1,
                ["department"] = "Engineering",
                ["status"] = i < 3 ? "VIP" : "Standard",
                ["amount"] = 100m
            });
        }
        // Group "Executive": only 2 rows, each amount = 1000 (must be suppressed because 2 < 5)
        for (int i = 0; i < 2; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = 100 + i,
                ["department"] = "Executive",
                ["status"] = "VIP",
                ["amount"] = 1000m
            });
        }

        var principal = CreatePrincipal("S-1-5-21-ANALYST");
        var decision = TableAccessDecision.Allowed(
            OrdersTableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = ColumnAccessLevel.Clear,
                ["status"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            });

        var service = new GovernedAggregationService(
            metadataRepository: metaRepo,
            dataSourceExecutors: [new InMemoryRowExecutor(rows)],
            analyticsOptions: Options.Create(new AnalyticsOptions { MinimumGroupSize = 5 }));

        // 1. Unfiltered query: "Engineering" (6 rows, sum=600) survives; "Executive" (2 rows, sum=2000) is suppressed.
        var unfilteredQuery = new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount")],
            PreEvaluatedDecision: decision);

        var result = await service.ExecuteAggregationAsync(principal, unfilteredQuery);

        result.Suppressed.ShouldBeTrue();
        result.SuppressedGroupsCount.ShouldBe(1);
        result.SuppressedRowsCount.ShouldBe(2);
        result.Groups.Count.ShouldBe(1);
        result.Groups[0].GroupKey["department"].ShouldBe("Engineering");
        result.Groups[0].Count.ShouldBe(6);
        result.Groups[0].Sum["amount"].ShouldBe(600m);

        // Differencing protection: Totals must equal ONLY the surviving group (600, NOT 2600!)
        result.Totals.ShouldNotBeNull();
        result.Totals!.Count.ShouldBe(6);
        result.Totals.Sum["amount"].ShouldBe(600m);

        // 2. Filtered query narrowing "Engineering" to 3 rows (status = 'VIP'): now BOTH groups are < 5 and suppressed!
        var narrowedQuery = new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount")],
            Where: TableFilterClause.FromTrinoText("status = 'VIP'", ["status"]),
            PreEvaluatedDecision: decision);

        var narrowedResult = await service.ExecuteAggregationAsync(principal, narrowedQuery);
        narrowedResult.Suppressed.ShouldBeTrue();
        narrowedResult.SuppressedGroupsCount.ShouldBe(2);
        narrowedResult.Groups.ShouldBeEmpty();
        narrowedResult.Totals!.Count.ShouldBe(0);
        narrowedResult.Totals.Sum["amount"].ShouldBe(0m);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_MergeIntoOtherStrategy_MergesWhenCombinedCountMeetsThreshold_AndSuppressesWhenBelow()
    {
        // Arrange: #37 - SmallGroupStrategy.MergeIntoOther
        var metadata = CreateOrdersMetadata(minGroupSize: 5, strategy: SmallGroupStrategy.MergeIntoOther);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        // "Sales": 5 rows (survives on its own)
        for (int i = 0; i < 5; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["department"] = "Sales", ["amount"] = 10m });
        }
        // "Legal": 3 rows (< 5)
        for (int i = 0; i < 3; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["department"] = "Legal", ["amount"] = 20m });
        }
        // "HR": 3 rows (< 5) -> combined Legal + HR = 6 rows >= 5 -> merged into "OTHER"
        for (int i = 0; i < 3; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["department"] = "HR", ["amount"] = 30m });
        }

        var principal = CreatePrincipal("S-1-5-21-ANALYST");
        var decision = TableAccessDecision.Allowed(
            OrdersTableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            });

        var service = new GovernedAggregationService(
            metadataRepository: metaRepo,
            dataSourceExecutors: [new InMemoryRowExecutor(rows)],
            analyticsOptions: Options.Create(new AnalyticsOptions
            {
                MinimumGroupSize = 5,
                SmallGroupStrategy = SmallGroupStrategy.MergeIntoOther
            }));

        // Case 1: Combined small groups (3 + 3 = 6 >= 5) merge into "OTHER"
        var mergedResult = await service.ExecuteAggregationAsync(
            principal,
            new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount")],
                SmallGroupStrategyOverride: SmallGroupStrategy.MergeIntoOther,
                PreEvaluatedDecision: decision));

        mergedResult.MergedIntoOther.ShouldBeTrue();
        mergedResult.Suppressed.ShouldBeFalse();
        mergedResult.Groups.Count.ShouldBe(2);

        var otherBucket = mergedResult.Groups.Single(g => g.IsMergedOther);
        otherBucket.GroupKey["department"].ShouldBe("OTHER");
        otherBucket.Count.ShouldBe(6);
        otherBucket.Sum["amount"].ShouldBe(150m); // 3*20 + 3*30
        mergedResult.Totals!.Count.ShouldBe(11);
        mergedResult.Totals.Sum["amount"].ShouldBe(200m);

        // Case 2: Filter out "HR" so only "Legal" (3 rows < 5) is small -> OTHER has only 3 rows < 5, so OTHER is suppressed!
        var suppressedOtherResult = await service.ExecuteAggregationAsync(
            principal,
            new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount")],
                Where: TableFilterClause.FromTrinoText("department <> 'HR'", ["department"]),
                SmallGroupStrategyOverride: SmallGroupStrategy.MergeIntoOther,
                PreEvaluatedDecision: decision));

        suppressedOtherResult.MergedIntoOther.ShouldBeFalse();
        suppressedOtherResult.Suppressed.ShouldBeTrue();
        suppressedOtherResult.SuppressedGroupsCount.ShouldBe(1);
        suppressedOtherResult.Groups.Count.ShouldBe(1);
        suppressedOtherResult.Groups[0].GroupKey["department"].ShouldBe("Sales");
        suppressedOtherResult.Totals!.Count.ShouldBe(5);
        suppressedOtherResult.Totals.Sum["amount"].ShouldBe(50m);
    }

    [Fact]
    public async Task SemanticMetricQueryService_EnforcesRls_MaskedColumns_MinGroupSize_MetricConsent_AndAudits()
    {
        // Arrange: #38 - dbt Semantic Layer Metric Governance
        var catalog = new InMemorySemanticCatalog();
        var ingestion = new DbtSemanticManifestIngestionService(catalog, NullLogger<DbtSemanticManifestIngestionService>.Instance);

        const string manifestJson = """
        {
          "version": "2.4.0",
          "semantic_models": [
            {
              "name": "orders_model",
              "description": "Governed orders semantic model",
              "node_relation": { "relation_name": "sales.public.orders" },
              "entities": [
                { "name": "order_id", "type": "primary" }
              ],
              "measures": [
                { "name": "total_revenue", "agg": "sum", "expr": "amount", "min_group_size": 5 },
                { "name": "order_count", "agg": "count", "expr": "1" },
                { "name": "secret_discount_total", "agg": "sum", "expr": "discount_secret" }
              ],
              "dimensions": [
                { "name": "region", "type": "categorical" },
                { "name": "category", "type": "categorical" }
              ]
            }
          ],
          "metrics": [
            {
              "name": "mrr",
              "type": "simple",
              "label": "Monthly Recurring Revenue",
              "description": "Sum of recurring order revenue",
              "dimensions": ["region", "category"],
              "allowed_time_granularities": ["month", "quarter", "year"],
              "min_group_size": 5,
              "type_params": { "measure": "total_revenue" },
              "meta": { "unit": "EUR", "model": "orders_model" }
            },
            {
              "name": "avg_order_value",
              "type": "ratio",
              "label": "Average Order Value",
              "dimensions": ["region", "category"],
              "type_params": { "numerator": "total_revenue", "denominator": "order_count" },
              "meta": { "unit": "EUR", "model": "orders_model" }
            },
            {
              "name": "masked_discount_metric",
              "type": "simple",
              "label": "Masked Discount Metric",
              "dimensions": ["category"],
              "type_params": { "measure": "secret_discount_total" },
              "meta": { "model": "orders_model" }
            }
          ]
        }
        """;

        await ingestion.ParseAndIngestAsync(manifestJson, new TenantId("tenant-a"));

        var metadata = CreateOrdersMetadata(minGroupSize: 5);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata);
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([metadata]);

        // 5 EU Software orders (amount = 100 each -> mrr = 500), 2 EU Hardware orders (amount = 50 each -> suppressed because 2 < 5),
        // and 6 US Software orders (amount = 200 each -> filtered out by EU caller's RLS!)
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (int i = 0; i < 5; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = i + 1,
                ["region"] = "EU",
                ["category"] = "Software",
                ["amount"] = 100m,
                ["discount_secret"] = 10m
            });
        }
        for (int i = 0; i < 2; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = 20 + i,
                ["region"] = "EU",
                ["category"] = "Hardware",
                ["amount"] = 50m,
                ["discount_secret"] = 5m
            });
        }
        for (int i = 0; i < 6; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = 50 + i,
                ["region"] = "US",
                ["category"] = "Software",
                ["amount"] = 200m,
                ["discount_secret"] = 20m
            });
        }

        // Caller holds ONLY a metric-scoped consent ("metric:mrr" and "metric:avg_order_value") with RLS row filter region = 'EU',
        // and does NOT hold full table consent on sales.public.orders!
        var metricCaller = CreatePrincipal("S-1-5-21-METRIC-CONSUMER");
        var consentRepo = Substitute.For<IConsentRepository>();
        var metricConsentMrr = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("metric", "default", "metric:mrr"),
            Effect = ConsentEffect.Allow,
            TenantId = new TenantId("tenant-a"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-METRIC-CONSUMER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "category", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "discount_secret", AccessLevel = ColumnAccessLevel.Mask }
            ],
            RowFilters =
            [
                new ConsentRowFilter
                {
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "\"EU\""
                }
            ]
        };
        var metricConsentAov = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("metric", "default", "metric:avg_order_value"),
            Effect = ConsentEffect.Allow,
            TenantId = new TenantId("tenant-a"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-METRIC-CONSUMER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = metricConsentMrr.ColumnRules,
            RowFilters = metricConsentMrr.RowFilters
        };
        var metricConsentMasked = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("metric", "default", "metric:masked_discount_metric"),
            Effect = ConsentEffect.Allow,
            TenantId = new TenantId("tenant-a"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-METRIC-CONSUMER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = metricConsentMrr.ColumnRules,
            RowFilters = metricConsentMrr.RowFilters
        };

        // Table-level consent lookup returns empty (no full table consent!)
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                OrdersTableId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns([metricConsentMrr, metricConsentAov, metricConsentMasked]);

        var resolutionService = new ConsentResolutionService(new RowFilterSqlBuilder());
        var auditRepo = new CapturingAuditLogRepository();

        var aggService = new GovernedAggregationService(
            metadataRepository: metaRepo,
            consentRepository: consentRepo,
            resolutionService: resolutionService,
            dataSourceExecutors: [new InMemoryRowExecutor(rows)],
            auditLogRepository: auditRepo,
            analyticsOptions: Options.Create(new AnalyticsOptions { MinimumGroupSize = 5 }));

        var metricService = new SemanticMetricQueryService(
            catalogService: catalog,
            aggregationService: aggService,
            metadataRepository: metaRepo,
            consentRepository: consentRepo,
            resolutionService: resolutionService,
            auditLogRepository: auditRepo);

        // 1. Execute governed metric query with metric-scoped consent, RLS (region = 'EU'), and MinGroupSize = 5
        var metricResult = await metricService.QueryMetricsAsync(
            metricCaller,
            new MetricQueryRequest(
                Metrics: ["mrr", "avg_order_value"],
                GroupBy: ["category"],
                TimeGranularity: "month"));

        metricResult.ManifestVersion.ShouldBe("2.4.0");
        metricResult.Suppressed.ShouldBeTrue();
        metricResult.SuppressedGroupsCount.ShouldBe(1); // EU Hardware (2 rows < 5) suppressed
        metricResult.Groups.Count.ShouldBe(1);

        var softwareBucket = metricResult.Groups[0];
        softwareBucket.GroupKey["category"].ShouldBe("Software");
        softwareBucket.Count.ShouldBe(5); // Only EU Software (5 rows), US Software (6 rows) excluded by RLS!
        softwareBucket.Values["mrr"].ShouldBe(500m);
        softwareBucket.Values["avg_order_value"].ShouldBe(100m);

        // Totals must reflect ONLY surviving EU Software bucket (500m)
        metricResult.Totals.ShouldNotBeNull();
        metricResult.Totals!.Values["mrr"].ShouldBe(500m);

        // 2. Querying a metric backed by a masked column throws MaskedAggregationException (AGGREGATION_ON_MASKED_COLUMN)
        var maskedEx = await Should.ThrowAsync<MaskedAggregationException>(() =>
            metricService.QueryMetricsAsync(
                metricCaller,
                new MetricQueryRequest(Metrics: ["masked_discount_metric"], GroupBy: ["category"])));
        maskedEx.ErrorCode.ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
        maskedEx.ColumnName.ShouldBe("discount_secret");

        // 3. Disallowed time granularity is rejected
        await Should.ThrowAsync<ArgumentException>(() =>
            metricService.QueryMetricsAsync(
                metricCaller,
                new MetricQueryRequest(Metrics: ["mrr"], GroupBy: ["category"], TimeGranularity: "hour")));

        // 4. Verify Audit Log contains METRIC_QUERY with metric names and manifest version
        var metricAudit = auditRepo.Entries.FirstOrDefault(e => e.EventType == AuditEventTypes.MetricQuery && e.Decision == "ALLOW");
        metricAudit.ShouldNotBeNull();
        metricAudit!.DetailsJson.ShouldContain("mrr");
        metricAudit.DetailsJson.ShouldContain("2.4.0");

        // 5. Verify MCP query_metrics tool execution
        McpSemanticTools.Definitions().ShouldContain(d => d.Name == McpSemanticTools.QueryMetrics);
        var mcpJson = await McpSemanticTools.ExecuteQueryMetricsAsync(
            metricService,
            metricCaller,
            """{ "metrics": ["mrr"], "groupBy": ["category"], "timeGranularity": "month" }""");
        using var mcpDoc = JsonDocument.Parse(mcpJson);
        mcpDoc.RootElement.GetProperty("manifestVersion").GetString().ShouldBe("2.4.0");
        mcpDoc.RootElement.GetProperty("groups").GetArrayLength().ShouldBe(1);
    }
}
