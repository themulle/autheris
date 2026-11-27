#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #682: <c>describe_dataset</c> and <c>describe_my_access</c> accept <c>graphQlField</c> as an alternative
/// to <c>dataset</c>, and <c>list_datasets</c> accepts an exact case-sensitive <c>graphQlField</c> filter.
/// Hidden/invisible datasets must answer identically to unknown fields (no enumeration oracle).
/// </summary>
public sealed class McpGraphQlFieldLookupTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier OrdersId = new("sales", "public", "orders");
    private static readonly TableIdentifier SecretId = new("hr", "private", "salaries");
    private const string OrdersField = "sales_public_orders";
    private const string SecretField = "hr_private_salaries";

    private static readonly McpSessionContext Session = new(
        "s1",
        "agent",
        Tenant,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        UserSid: "S-1-USER");

    private static McpToolDefinition Tool(string name) => McpDatasetTools.Definitions().Single(t => t.Name == name);

    private static TableMetadata MakeTable(TableIdentifier id, bool discoveryHidden = false) => new()
    {
        Identifier = id,
        Table = new Table
        {
            TableName = id.TableName,
            SchemaName = id.Schema,
            Description = "Test table",
            DiscoveryHidden = discoveryHidden
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" }
        ]
    };

    private static IGraphQlCatalogMap MakeMap()
    {
        var map = Substitute.For<IGraphQlCatalogMap>();
        map.ResolveRootFieldAsync(OrdersField, Arg.Any<CancellationToken>()).Returns(OrdersId);
        map.ResolveRootFieldAsync(SecretField, Arg.Any<CancellationToken>()).Returns(SecretId);
        map.GetRootFieldAsync(OrdersId, Arg.Any<CancellationToken>()).Returns(OrdersField);
        map.GetRootFieldAsync(SecretId, Arg.Any<CancellationToken>()).Returns(SecretField);
        map.GetTableAsync(OrdersId, Arg.Any<CancellationToken>()).Returns(
            new GraphQlTableMapping(
                OrdersId,
                OrdersField,
                "SalesPublicOrders",
                "SalesPublicOrdersFilterInput",
                "SalesPublicOrdersSortInput",
                [new GraphQlColumnMapping("id", "id", "Int"), new GraphQlColumnMapping("amount", "amount", "Decimal")],
                []));
        map.GetTableAsync(SecretId, Arg.Any<CancellationToken>()).Returns(
            new GraphQlTableMapping(
                SecretId,
                SecretField,
                "HrPrivateSalaries",
                "HrPrivateSalariesFilterInput",
                "HrPrivateSalariesSortInput",
                [new GraphQlColumnMapping("id", "id", "Int")],
                []));
        return map;
    }

    private static GatewayMcpQueryExecutor CreateExecutor(bool useDiscovery = true, bool secretVisible = false)
    {
        var ordersMeta = MakeTable(OrdersId);
        var secretMeta = MakeTable(SecretId, discoveryHidden: !secretVisible);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.StubAllTables(Task.FromResult<IReadOnlyList<TableMetadata>>([ordersMeta, secretMeta]));
        metadataRepo.ServeSingleReadsFromCatalog();

        var consentRepo = Substitute.For<IConsentRepository>();
        var userSid = new Sid("S-1-USER");
        var tenantId = new TenantId(Tenant);
        var grant = new Consent
        {
            TableIdentifier = OrdersId,
            TenantId = tenantId,
            Effect = ConsentEffect.Allow,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
            ]
        };

        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([grant]));
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IReadOnlyCollection<Sid>>(), OrdersId, Arg.Any<DateTimeOffset>(), tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([grant]));
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IReadOnlyCollection<Sid>>(), SecretId, Arg.Any<DateTimeOffset>(), tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var resolution = new ConsentResolutionService();
        var explanationService = new EffectiveAccessExplanationService(
            metadataRepo,
            consentRepo,
            resolution,
            ownershipRepository: null,
            Options.Create(new GatewayOptions()));

        IDiscoveryService? discovery = null;
        if (useDiscovery)
        {
            discovery = Substitute.For<IDiscoveryService>();
            var ordersObj = new DiscoveryObject(
                DiscoveryObjectKind.Table,
                OrdersId.Domain,
                OrdersId.Schema,
                OrdersId.TableName,
                "Orders",
                "Test table",
                "NORMAL",
                [],
                [],
                DiscoveryAccessState.Granted,
                null,
                [
                    new DiscoveryColumn("id", null, "int", null, false, true, DiscoveryColumnState.Clear, null),
                    new DiscoveryColumn("amount", null, "decimal", null, false, false, DiscoveryColumnState.Clear, null)
                ],
                null,
                false,
                false);
            discovery.GetAsync(OrdersId, Arg.Any<CatalogCaller>(), Arg.Any<CancellationToken>()).Returns(ordersObj);
            discovery.GetAsync(SecretId, Arg.Any<CatalogCaller>(), Arg.Any<CancellationToken>()).Returns((DiscoveryObject?)null);
            discovery.ListAsync(Arg.Any<DiscoveryQuery>(), Arg.Any<CatalogCaller>(), Arg.Any<CancellationToken>())
                .Returns(new DiscoveryPage([ordersObj], 1, false));
        }

        var map = MakeMap();
        var gatewayOptions = new GatewayOptions();
        var catalog = new McpDatasetCatalog(
            metadataRepo,
            consentRepo,
            Substitute.For<IGatewayExecutionService>(),
            goldenQueryService: null,
            options: Options.Create(gatewayOptions),
            graphQlMap: map,
            explanationService: explanationService,
            visibilityService: CatalogVisibilityTestFactory.Create(metadataRepo, consentRepo, gatewayOptions),
            discoveryService: discovery);

        return new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(),
            Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            McpRowCapTests.Options(100),
            persistedToolValidator: new PersistedToolValidator(),
            datasetCatalog: catalog);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescribeDataset_ResolvesByExactGraphQlField(bool useDiscovery)
    {
        var executor = CreateExecutor(useDiscovery: useDiscovery);

        var json = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeDataset),
            $$"""{"graphQlField":"{{OrdersField}}"}""",
            Session);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("isError", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("id").GetString().ShouldBe("sales.public.orders");
        doc.RootElement.GetProperty("graphQl").GetProperty("queryField").GetString().ShouldBe(OrdersField);
    }

    [Fact]
    public async Task DescribeDataset_GraphQlField_IsCaseSensitive()
    {
        var executor = CreateExecutor(useDiscovery: true);

        var wrongCase = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeDataset),
            """{"graphQlField":"SALES_PUBLIC_ORDERS"}""",
            Session);

        using var doc = JsonDocument.Parse(wrongCase);
        doc.RootElement.TryGetProperty("isError", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("DATASET_NOT_FOUND");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public async Task DescribeDataset_InvisibleGraphQlField_MatchesUnknownIdentically(bool useDiscovery)
    {
        var executor = CreateExecutor(useDiscovery: useDiscovery, secretVisible: false);

        var invisibleJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeDataset),
            $$"""{"graphQlField":"{{SecretField}}"}""",
            Session);
        var unknownJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeDataset),
            """{"graphQlField":"nonexistent_root_field"}""",
            Session);

        invisibleJson.ShouldBe(unknownJson);
        using var doc = JsonDocument.Parse(invisibleJson);
        doc.RootElement.TryGetProperty("isError", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("DATASET_NOT_FOUND");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public async Task Guardrail_DescribeDataset_InvisibleAndUnknownGraphQlField_AreIdentical(bool brief)
    {
        var executor = CreateExecutor(useDiscovery: true, secretVisible: false);
        var pdp = PdpTestDoubles.From(ctx =>
            TableAccessDecision.Denied(ctx.Object.Identifier, "No consent."));

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions())),
            Options.Create(new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: executor,
            decisionPoint: pdp,
            tableMetadataRepository: PdpTestDoubles.AnyTable(),
            graphQlCatalogMap: MakeMap());

        var briefArg = brief ? ""","brief":true""" : string.Empty;
        var invisible = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, $$"""{"graphQlField":"{{SecretField}}"{{briefArg}}}"""),
            Session);
        var unknown = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, $$"""{"graphQlField":"does_not_exist"{{briefArg}}}"""),
            Session);

        invisible.IsSuccess.ShouldBeFalse();
        invisible.IsSuccess.ShouldBe(unknown.IsSuccess);
        invisible.ContentJson.ShouldBe(unknown.ContentJson);
        invisible.ErrorMessage.ShouldBe(unknown.ErrorMessage);
    }

    [Fact]
    public async Task DescribeMyAccess_ResolvesByExactGraphQlField()
    {
        var executor = CreateExecutor(useDiscovery: true);

        var json = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeMyAccess),
            $$"""{"graphQlField":"{{OrdersField}}"}""",
            Session);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("isError", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("table").GetString().ShouldBe("sales.public.orders");
        doc.RootElement.GetProperty("decision").GetString().ShouldBe("ALLOWED");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DescribeMyAccess_InvisibleGraphQlField_DoesNotLeakTableIdAndMatchesUnknown()
    {
        var executor = CreateExecutor(useDiscovery: true, secretVisible: false);

        var invisibleJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeMyAccess),
            $$"""{"graphQlField":"{{SecretField}}"}""",
            Session);
        var unknownJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.DescribeMyAccess),
            """{"graphQlField":"does_not_exist"}""",
            Session);

        invisibleJson.ShouldNotContain("hr.private.salaries");

        using var invDoc = JsonDocument.Parse(invisibleJson);
        using var unkDoc = JsonDocument.Parse(unknownJson);
        invDoc.RootElement.GetProperty("decision").GetString().ShouldBe("DENIED");
        unkDoc.RootElement.GetProperty("decision").GetString().ShouldBe("DENIED");
        invDoc.RootElement.GetProperty("consentDescription").GetString()
            .ShouldBe(unkDoc.RootElement.GetProperty("consentDescription").GetString());
        invDoc.RootElement.GetProperty("table").GetString().ShouldBe(SecretField);
        unkDoc.RootElement.GetProperty("table").GetString().ShouldBe("does_not_exist");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListDatasets_GraphQlFieldFilter_ReturnsExactMatchOnly(bool useDiscovery)
    {
        var executor = CreateExecutor(useDiscovery: useDiscovery);

        var hitJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.ListDatasets),
            $$"""{"graphQlField":"{{OrdersField}}"}""",
            Session);
        using var hitDoc = JsonDocument.Parse(hitJson);
        hitDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        var datasets = hitDoc.RootElement.GetProperty("datasets").EnumerateArray().ToList();
        datasets.ShouldHaveSingleItem().GetProperty("id").GetString().ShouldBe("sales.public.orders");

        var wrongCaseJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.ListDatasets),
            """{"graphQlField":"SALES_PUBLIC_ORDERS"}""",
            Session);
        using var wrongDoc = JsonDocument.Parse(wrongCaseJson);
        wrongDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        wrongDoc.RootElement.GetProperty("datasets").GetArrayLength().ShouldBe(0);

        var hiddenJson = await executor.ExecuteOperationAsync(
            Tool(McpDatasetTools.ListDatasets),
            $$"""{"graphQlField":"{{SecretField}}"}""",
            Session);
        using var hiddenDoc = JsonDocument.Parse(hiddenJson);
        hiddenDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        hiddenDoc.RootElement.GetProperty("datasets").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData(McpDatasetTools.DescribeDataset, """{"dataset":"sales.public.orders","graphQlField":"sales_public_orders"}""")]
    [InlineData(McpDatasetTools.DescribeDataset, """{}""")]
    [InlineData(McpDatasetTools.DescribeMyAccess, """{"dataset":"sales.public.orders","graphQlField":"sales_public_orders"}""")]
    public async Task DescribeTools_InvalidDatasetOrGraphQlFieldCombinations_FailWithInvalidParams(string toolName, string args)
    {
        var executor = CreateExecutor(useDiscovery: true);

        var json = await executor.ExecuteOperationAsync(Tool(toolName), args, Session);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("isError", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
    }
}
