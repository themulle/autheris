namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpSqlNameTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier MixedCaseId = new("salesdb", "SalesSchema", "OrdersView");
    private static readonly TableIdentifier SecretId = new("salesdb", "PrivateSchema", "HiddenSalaries");

    private static readonly McpSessionContext Session = new(
        "s1",
        "agent",
        Tenant,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        UserSid: "S-1-USER");

    private static McpToolDefinition Tool(string name) => McpDatasetTools.Definitions().Single(t => t.Name == name);

    private static GatewayOptions MixedCaseWebSqlOptions() => new()
    {
        WebSql = new WebSqlOptions
        {
            Enabled = true,
            DefaultDataSourceName = "SalesDb",
            AllowedDataSources = ["SalesDb"]
        }
    };

    private static TableMetadata MakeMixedCaseTable(TableIdentifier id, bool discoveryHidden = false) => new()
    {
        Identifier = id,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = id.Domain,
            TableName = id.TableName,
            SchemaName = id.Schema,
            DisplayName = id.TableName,
            Description = "Mixed case view",
            Sensitivity = "NORMAL",
            IsActive = true,
            DiscoveryHidden = discoveryHidden
        },
        OwnerTenantId = Tenant,
        PrimaryKeyColumns = ["OrderId"],
        Columns =
        [
            new TableColumn { ColumnName = "OrderId", DataType = "int" },
            new TableColumn { ColumnName = "CustomerEmail", DataType = "varchar" }
        ]
    };

    private static (McpDatasetCatalog Catalog, GatewayMcpQueryExecutor Executor, FakeDiscoveryTableRepository Tables) CreateHarness(
        GatewayOptions? opts = null,
        bool withDiscovery = true)
    {
        var tables = new FakeDiscoveryTableRepository();
        tables.Tables.Add(MakeMixedCaseTable(MixedCaseId));
        tables.Tables.Add(MakeMixedCaseTable(SecretId, discoveryHidden: true));

        var consents = new FakeDiscoveryConsentRepository();
        consents.Active.Add(new Consent
        {
            Id = Guid.NewGuid(),
            GranteeSid = new Sid("S-1-USER"),
            TenantId = new TenantId(Tenant),
            TableIdentifier = MixedCaseId,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "OrderId", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "CustomerEmail", AccessLevel = ColumnAccessLevel.Clear }
            ]
        });

        var gatewayOptions = opts ?? MixedCaseWebSqlOptions();
        var effectiveOpts = Options.Create(gatewayOptions);
        var pdp = FakeAccessDecisionPoint.AllowAll();
        var discovery = withDiscovery
            ? new DiscoveryService(
                tables,
                pdp,
                consents,
                new FakeDiscoveryApprovalRepository(),
                new FakeDiscoveryOwnershipRepository(),
                new FakeDiscoveryArtifactStore(),
                new FakeAuditWriter(),
                effectiveOpts,
                Options.Create(new CatalogDiscoveryOptions()),
                logger: NullLogger<DiscoveryService>.Instance)
            : null;

        var visibility = CatalogVisibilityTestFactory.Create(tables, consents, gatewayOptions);
        var explanation = new EffectiveAccessExplanationService(
            tables,
            consents,
            new ConsentResolutionService(),
            ownershipRepository: null,
            effectiveOpts);

        var catalog = new McpDatasetCatalog(
            tables,
            consents,
            Substitute.For<IGatewayExecutionService>(),
            goldenQueryService: null,
            options: effectiveOpts,
            explanationService: explanation,
            visibilityService: visibility,
            discoveryService: discovery);

        var executor = new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(),
            Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            McpRowCapTests.Options(100),
            persistedToolValidator: new PersistedToolValidator(),
            datasetCatalog: catalog);

        return (catalog, executor, tables);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListDatasets_And_DescribeDataset_PreserveMixedCaseSqlName_OnDatasetAndColumns(bool withDiscovery)
    {
        var (catalog, _, _) = CreateHarness(withDiscovery: withDiscovery);
        var user = DiscoveryTestBuilder.User("S-1-USER", Tenant);

        var list = await catalog.ListDatasetsAsync(user);
        var summary = list.Datasets.ShouldHaveSingleItem();
        summary.SqlName.ShouldBe("SalesDb.SalesSchema.OrdersView");

        var described = await catalog.DescribeDatasetAsync(user, summary.Id);
        described.SqlName.ShouldBe("SalesDb.SalesSchema.OrdersView");
        described.Columns.Select(c => c.SqlName).ShouldBe(["OrderId", "CustomerEmail"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescribeDataset_BySqlName_ResolvesMixedCaseDataset_AndHidesInvisibleOnes(bool withDiscovery)
    {
        var (catalog, _, _) = CreateHarness(withDiscovery: withDiscovery);
        var user = DiscoveryTestBuilder.User("S-1-USER", Tenant);

        var described = await catalog.DescribeDatasetAsync(
            user,
            dataset: null,
            graphQlField: null,
            sqlName: "SalesDb.SalesSchema.OrdersView",
            language: null,
            brief: false);

        described.SqlName.ShouldBe("SalesDb.SalesSchema.OrdersView");
        described.Table.ShouldBe("OrdersView");
        described.Schema.ShouldBe("SalesSchema");

        await Should.ThrowAsync<TableNotFoundException>(() =>
            catalog.DescribeDatasetAsync(
                user,
                dataset: null,
                graphQlField: null,
                sqlName: "SalesDb.PrivateSchema.HiddenSalaries",
                language: null,
                brief: false));

        await Should.ThrowAsync<ArgumentException>(() =>
            catalog.DescribeDatasetAsync(
                user,
                dataset: "salesdb.salesschema.ordersview",
                graphQlField: null,
                sqlName: "SalesDb.SalesSchema.OrdersView",
                language: null,
                brief: false));
    }

    [Fact]
    public async Task DescribeMyAccess_BySqlName_ExplainsAccessAndRejectsConflictingArgs()
    {
        var (catalog, _, _) = CreateHarness();
        var user = DiscoveryTestBuilder.User("S-1-USER", Tenant);

        var access = await catalog.DescribeMyAccessAsync(
            user,
            dataset: null,
            graphQlField: null,
            sqlName: "SalesDb.SalesSchema.OrdersView");

        access.Decision.ShouldBe("ALLOWED");
        access.AllowedColumns.ShouldContain("OrderId");
        access.AllowedColumns.ShouldContain("CustomerEmail");

        await Should.ThrowAsync<ArgumentException>(() =>
            catalog.DescribeMyAccessAsync(
                user,
                dataset: "salesdb.salesschema.ordersview",
                graphQlField: null,
                sqlName: "SalesDb.SalesSchema.OrdersView"));
    }

    [Fact]
    public async Task SearchCatalog_EmitsSqlNameOnDatasetAndMatchedColumns()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.SearchCatalogDetailedAsync(
                Arg.Any<CatalogSearchQuery>(),
                Arg.Any<Autheris.Domain.Model.RequestContext>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new CatalogSearchHit(
                    TableIdentifier: MixedCaseId,
                    Domain: MixedCaseId.Domain,
                    DisplayName: "Orders View",
                    Description: "Orders",
                    Language: "en",
                    CombinedScore: 0.95,
                    VectorScore: null,
                    Bm25Score: 0.95,
                    MatchedTerms: ["OrdersView"],
                    RelevantColumns: ["OrderId", "CustomerEmail"],
                    RelatedJoinPaths: [],
                    SuggestedGraphQlField: null,
                    Sensitivity: "NORMAL",
                    AccessStatus: "GRANTED")
            ]);

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            discovery,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            new FakeDiscoveryTableRepository(),
            gatewayOptions: Options.Create(MixedCaseWebSqlOptions()));

        var json = await handler.ExecuteToolAsync(
            Tool(McpDatasetTools.SearchCatalog),
            """{"query":"OrdersView"}""",
            Session);

        using var doc = JsonDocument.Parse(json);
        var hit = doc.RootElement.GetProperty("datasets")[0];
        hit.GetProperty("sqlName").GetString().ShouldBe("SalesDb.SalesSchema.OrdersView");
        var cols = hit.GetProperty("columns");
        cols.GetArrayLength().ShouldBe(2);
        cols[0].GetProperty("name").GetString().ShouldBe("OrderId");
        cols[0].GetProperty("sqlName").GetString().ShouldBe("OrderId");
        cols[1].GetProperty("name").GetString().ShouldBe("CustomerEmail");
        cols[1].GetProperty("sqlName").GetString().ShouldBe("CustomerEmail");
    }

    [Fact]
    public async Task Guardrail_And_Executor_SupportDescribeDatasetBySqlName()
    {
        var (_, executor, tables) = CreateHarness();
        var options = Options.Create(MixedCaseWebSqlOptions());
        var pdp = FakeAccessDecisionPoint.AllowAll();
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(options),
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: executor,
            decisionPoint: pdp,
            tableMetadataRepository: tables);

        var args = """{"sqlName":"SalesDb.SalesSchema.OrdersView"}""";
        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, args),
            Session);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        using var doc = JsonDocument.Parse(result.ContentJson);
        doc.RootElement.GetProperty("sqlName").GetString().ShouldBe("SalesDb.SalesSchema.OrdersView");
        doc.RootElement.GetProperty("columns")[0].GetProperty("sqlName").GetString().ShouldBe("OrderId");
    }
}
