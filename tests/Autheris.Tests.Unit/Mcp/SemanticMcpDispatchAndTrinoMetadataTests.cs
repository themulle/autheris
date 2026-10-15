namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public sealed class SemanticMcpDispatchAndTrinoMetadataTests
{
    private static readonly DbtMetric SampleMetric = new(
        Name: "monthly_recurring_revenue",
        Type: "simple",
        Description: "Total monthly recurring revenue",
        Label: "MRR",
        Filter: null,
        TypeParams: null,
        Dimensions: ["region", "order_date"],
        Measures: ["revenue_sum"],
        Meta: new Dictionary<string, string>(),
        Unit: "EUR",
        Version: "1.2");

    private static readonly DbtSemanticModel SampleModel = new(
        Name: "orders",
        Description: "Governed orders semantic model",
        NodeRelation: "sales.public.orders",
        Entities: [new DbtEntity("order_id", "primary", "Primary order identifier")],
        Measures: [new DbtMeasure("revenue_sum", "sum", "amount", "Total revenue"), new DbtMeasure("order_count", "count", "order_id", "Order count")],
        Dimensions: [new DbtDimension("region", "categorical", "Customer region"), new DbtDimension("order_date", "time", "Order date", TimeGranularity: "day")]);

    [Fact]
    public void McpToolRegistry_ShouldRegister_SemanticTools()
    {
        var registry = new McpToolRegistry();

        var listTool = registry.FindTool(McpSemanticTools.ListMetrics);
        var describeTool = registry.FindTool(McpSemanticTools.DescribeMetric);
        var queryTool = registry.FindTool(McpSemanticTools.QueryMetrics);

        listTool.Should().NotBeNull();
        listTool!.TargetTable.Should().Be(McpSemanticTools.SemanticCatalogTable);
        describeTool.Should().NotBeNull();
        describeTool!.TargetTable.Should().Be(McpSemanticTools.SemanticCatalogTable);
        queryTool.Should().NotBeNull();
        queryTool!.TargetTable.Should().Be(McpSemanticTools.SemanticCatalogTable);
    }

    [Fact]
    public async Task McpToolExecutionHandler_And_AiDataGuardrailService_ShouldExecute_SemanticTools()
    {
        var catalog = new InMemorySemanticCatalog();
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([SampleModel], [SampleMetric], "1.2"));

        var metricQueryService = Substitute.For<ISemanticMetricQueryService>();
        metricQueryService.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MetricQueryResult(
                Metrics: ["monthly_recurring_revenue"],
                ManifestVersion: "1.2",
                Groups: Array.Empty<AggregationGroupRow>(),
                Totals: null,
                Metadata: new AggregationMetadata(
                    MinimumGroupSize: 5,
                    Strategy: SmallGroupStrategy.Suppress,
                    Suppressed: false,
                    SuppressedGroupsCount: 0,
                    SuppressedRowsCount: 0,
                    MergedIntoOther: false,
                    Truncated: false),
                MetricDefinitions: new Dictionary<string, DbtMetric> { ["monthly_recurring_revenue"] = SampleMetric }));

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            Substitute.For<ICatalogDiscoveryService>(),
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<ITableMetadataRepository>(),
            catalog,
            metricQueryService);

        handler.CanHandle(McpSemanticTools.ListMetrics).Should().BeTrue();
        handler.CanHandle(McpSemanticTools.DescribeMetric).Should().BeTrue();
        handler.CanHandle(McpSemanticTools.QueryMetrics).Should().BeTrue();

        var queryExecutor = Substitute.For<IMcpQueryExecutor>();
        queryExecutor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.ExecuteToolAsync(ci.Arg<McpToolDefinition>(), ci.Arg<string>(), ci.Arg<McpSessionContext>(), ci.Arg<CancellationToken>()));

        var options = Options.Create(new GatewayOptions
        {
            Casbin = new CasbinOptions { Enabled = false }
        });

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(),
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            Substitute.For<IAuditLogRepository>(),
            queryExecutor: queryExecutor);

        var session = new McpSessionContext(
            "sess-1",
            "sp-agent",
            "tenant-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-100");

        var listResult = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpSemanticTools.ListMetrics, "{}"),
            session);
        listResult.IsSuccess.Should().BeTrue();
        listResult.ContentJson.Should().Contain("monthly_recurring_revenue");

        var describeResult = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpSemanticTools.DescribeMetric, """{"metric":"monthly_recurring_revenue"}"""),
            session);
        describeResult.IsSuccess.Should().BeTrue();
        describeResult.ContentJson.Should().Contain("Total monthly recurring revenue");

        var queryResult = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpSemanticTools.QueryMetrics, """{"metrics":["monthly_recurring_revenue"],"groupBy":["region"]}"""),
            session);
        queryResult.IsSuccess.Should().BeTrue();
        queryResult.ContentJson.Should().Contain("monthly_recurring_revenue");
    }

    [Fact]
    public async Task InMemorySemanticCatalog_ShouldIsolateManifests_BetweenTenants()
    {
        var catalog = new InMemorySemanticCatalog();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        var metricA = SampleMetric with { Name = "tenant_a_mrr" };
        var metricB = SampleMetric with { Name = "tenant_b_arr" };

        await catalog.RegisterManifestAsync(new DbtSemanticManifest([SampleModel], [metricA], "1.0"), tenantA);
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([SampleModel], [metricB], "2.0"), tenantB);

        var listA = await catalog.ListMetricsAsync(null, tenantA);
        var listB = await catalog.ListMetricsAsync(null, tenantB);

        listA.Select(m => m.Name).Should().ContainSingle().Which.Should().Be("tenant_a_mrr");
        listB.Select(m => m.Name).Should().ContainSingle().Which.Should().Be("tenant_b_arr");
        (await catalog.GetMetricAsync("tenant_b_arr", tenantA)).Should().BeNull();
        (await catalog.GetMetricAsync("tenant_a_mrr", tenantB)).Should().BeNull();
    }

    [Fact]
    public async Task DbtSemanticManifestIngestionService_ShouldRegister_SemanticCatalogView_WhenRepositoryProvided()
    {
        var catalog = new InMemorySemanticCatalog();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        TableMetadata? capturedMeta = null;
        tableRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => capturedMeta = m), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<TableMetadata>()));

        var ingestion = new DbtSemanticManifestIngestionService(
            catalog,
            NullLogger<DbtSemanticManifestIngestionService>.Instance,
            tableRepo);

        const string manifestJson = """
        {
          "version": "1.1",
          "semantic_models": [
            {
              "name": "orders",
              "description": "Orders semantic model",
              "node_relation": { "relation_name": "sales.public.orders" },
              "entities": [
                { "name": "order_id", "type": "primary" }
              ],
              "dimensions": [
                { "name": "region", "type": "categorical", "description": "Region" },
                { "name": "ordered_at", "type": "time", "description": "Order timestamp" }
              ],
              "measures": [
                { "name": "revenue", "agg": "sum", "description": "Total revenue" },
                { "name": "order_count", "agg": "count", "description": "Count of orders" }
              ]
            }
          ],
          "metrics": []
        }
        """;

        await ingestion.ParseAndIngestAsync(manifestJson);

        capturedMeta.Should().NotBeNull();
        capturedMeta!.Identifier.Should().Be(new TableIdentifier("semantic", "semantic", "orders"));
        capturedMeta.Table.Description.Should().Be("Orders semantic model");
        capturedMeta.Table.Tags.Should().Contain("dbt");
        capturedMeta.Table.Tags.Should().Contain("semantic-model");
        var columnPairs = capturedMeta.Columns.Select(c => $"{c.ColumnName}:{c.DataType}").ToList();
        columnPairs.Should().Contain("order_id:varchar");
        columnPairs.Should().Contain("region:varchar");
        columnPairs.Should().Contain("ordered_at:timestamp");
        columnPairs.Should().Contain("revenue:decimal");
        columnPairs.Should().Contain("order_count:bigint");
    }

    [Fact]
    public async Task TrinoMetadataQueryHandler_ShouldFilterMetadata_ByCallerVisibility()
    {
        var visibleTableId = new TableIdentifier("sales", "public", "orders");
        var hiddenTableId = new TableIdentifier("hr", "confidential", "salaries");

        var visibleTable = new TableMetadata
        {
            Identifier = visibleTableId,
            Table = new Table { SchemaName = "public", TableName = "orders", IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var hiddenTable = new TableMetadata
        {
            Identifier = hiddenTableId,
            Table = new Table { SchemaName = "confidential", TableName = "salaries", IsActive = true },
            Columns = [new TableColumn { ColumnName = "salary", DataType = "decimal" }],
            PrimaryKeyColumns = ["salary"]
        };

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<TableMetadata>[] { [visibleTable, hiddenTable] }[0]);

        var tenantId = new TenantId("tenant-a");
        var userSid = new Sid("S-1-5-21-200");
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<DateTimeOffset>(),
                tenantId,
                Arg.Any<CancellationToken>())
            .Returns([
                new Consent
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    GranteeSid = userSid,
                    TableIdentifier = visibleTableId,
                    Effect = ConsentEffect.Allow
                }
            ]);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, userSid.Value),
            new Claim("tid", tenantId.Value)
        ], "Bearer"));

        var catalogsResult = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SHOW CATALOGS;", user, tenantId, tableRepo, consentRepo);
        catalogsResult.Should().NotBeNull();
        catalogsResult!.Columns.Should().BeEquivalentTo(["Catalog"]);
        catalogsResult.Rows.Select(r => r["Catalog"]?.ToString()).Should().BeEquivalentTo(["sales", "system"]);

        var schemasResult = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SHOW SCHEMAS FROM sales", user, tenantId, tableRepo, consentRepo);
        schemasResult.Should().NotBeNull();
        schemasResult!.Rows.Select(r => r["Schema"]?.ToString()).Should().BeEquivalentTo(["information_schema", "public"]);

        var tablesResult = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SHOW TABLES FROM sales.public", user, tenantId, tableRepo, consentRepo);
        tablesResult.Should().NotBeNull();
        tablesResult!.Rows.Select(r => r["Table"]?.ToString()).Should().ContainSingle().Which.Should().Be("orders");

        var colsResult = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SELECT * FROM information_schema.columns", user, tenantId, tableRepo, consentRepo);
        colsResult.Should().NotBeNull();
        colsResult!.Columns.Should().BeEquivalentTo(["table_catalog", "table_schema", "table_name", "column_name", "ordinal_position", "is_nullable", "data_type"]);
        colsResult.Rows.Select(r => r["column_name"]?.ToString()).Should().BeEquivalentTo(["id", "amount"]);
        colsResult.Rows.Select(r => r["table_name"]?.ToString()).Should().NotContain("salaries");
    }
}
